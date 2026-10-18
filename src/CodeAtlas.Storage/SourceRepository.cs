using System.Collections.Immutable;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using CodeAtlas.Contracts;
using CodeAtlas.Framework;

[assembly: AtlasModule("atlas.reports.store", "源码读取与报告存储", ArchitecturalRole.DataAccess)]

namespace CodeAtlas.Storage;

/// <summary>盘点仓库中的源码与配置文件；读取失败的文件保留位置，不执行仓库中的项目。</summary>
[AtlasComponent("atlas.analysis.inventory", ArchitecturalRole.DataAccess)]
[AtlasProvides(typeof(ISourceRepository))]
public sealed class SourceRepository : ISourceRepository
{
    /// <summary>扫描时跳过版本库、构建产物与第三方依赖目录。</summary>
    private static readonly HashSet<string> IgnoredDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "bin", "obj", "artifacts", "node_modules", "packages", ".vs"
    };

    /// <summary>本轮盘点保留的源码和配置扩展名；不支持语义的类型仍保留原文。</summary>
    private static readonly HashSet<string> CodeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".razor", ".xaml", ".js", ".ts", ".tsx", ".jsx", ".py", ".cpp", ".h", ".vb", ".fs", ".csproj", ".ps1"
    };

    /// <summary>遍历仓库，排除构建产物与链接，将源码原文和不可读取项组成快照。</summary>
    /// <param name="root">要分析的仓库根目录。</param>
    /// <param name="cancellationToken">中止本次文件读取的取消信号。</param>
    /// <returns>包含项目归属、原文和读取诊断的源码快照。</returns>
    public async Task<SourceSnapshot> ReadAsync(string root, CancellationToken cancellationToken = default)
    {
        string absoluteRoot = Path.GetFullPath(root);
        if (!Directory.Exists(absoluteRoot))
        {
            throw new DirectoryNotFoundException(absoluteRoot);
        }

        var documents = ImmutableArray.CreateBuilder<SourceDocument>();
        var findings = ImmutableArray.CreateBuilder<AnalysisFinding>();
        var directories = new Stack<string>();
        directories.Push(absoluteRoot);

        while (directories.TryPop(out string? directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string[] entries;
            try
            {
                entries = Directory.GetFileSystemEntries(directory);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                findings.Add(Unsupported("AT201", absoluteRoot, directory, "无法读取目录：" + error.Message));
                continue;
            }

            foreach (string path in entries.Order(StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                FileAttributes attributes;
                try { attributes = File.GetAttributes(path); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    findings.Add(Unsupported("AT201", absoluteRoot, path, "无法读取文件属性：" + error.Message));
                    continue;
                }
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    findings.Add(Unsupported("AT202", absoluteRoot, path, "暂不跟随符号链接/重解析点，避免重复或越界扫描。"));
                    continue;
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (!IgnoredDirectories.Contains(Path.GetFileName(path)))
                    {
                        directories.Push(path);
                    }
                    continue;
                }

                if (Path.GetExtension(path).Equals(".csproj", StringComparison.OrdinalIgnoreCase))
                {
                    InspectProject(path, absoluteRoot, findings);
                }

                if (!CodeExtensions.Contains(Path.GetExtension(path)))
                {
                    continue;
                }

                string relative = Path.GetRelativePath(absoluteRoot, path).Replace('\\', '/');
                try
                {
                    string text = await File.ReadAllTextAsync(path, new UTF8Encoding(false, true), cancellationToken);
                    documents.Add(new(relative, FindProject(path, absoluteRoot), Path.GetExtension(path).ToLowerInvariant(), text));
                }
                catch (DecoderFallbackException)
                {
                    findings.Add(Unsupported("AT203", absoluteRoot, path, "暂不支持非 UTF-8 源码；文件位置保留，请显式转换编码后分析。"));
                    documents.Add(new(relative, "[未确定项目]", "unsupported-encoding", "[无法可靠解码；原文件未修改]"));
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    findings.Add(Unsupported("AT201", absoluteRoot, path, "无法读取文件：" + error.Message));
                    documents.Add(new(relative, "[未确定项目]", "unreadable", "[原始位置保留，内容暂不可读取]"));
                }
            }
        }

        return new(absoluteRoot, documents.OrderBy(item => item.Path, StringComparer.Ordinal).ToImmutableArray(), findings.ToImmutable());
    }

    /// <summary>从文件所在目录向仓库根目录查找唯一项目；不跨越根目录或猜测歧义归属。</summary>
    /// <param name="path">待确定归属的源文件路径。</param>
    /// <param name="root">本次扫描的根目录边界。</param>
    /// <returns>相对项目路径；无法唯一确定时返回明确的未确定标记。</returns>
    private static string FindProject(string path, string root)
    {
        string? directory = Path.GetDirectoryName(path);
        while (directory is not null)
        {
            string[] projects = Directory.GetFiles(directory, "*.csproj");
            if (projects.Length == 1)
            {
                return Path.GetRelativePath(root, projects[0]).Replace('\\', '/');
            }
            if (projects.Length > 1 || string.Equals(directory, root, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
            directory = Path.GetDirectoryName(directory);
        }
        return "[未确定项目]";
    }

    /// <summary>识别项目中的条件、外部包和自定义编译配置，将当前无法求值的配置记录为限制。</summary>
    /// <param name="path">项目文件路径。</param>
    /// <param name="root">用于计算相对源码位置的仓库根目录。</param>
    /// <param name="findings">接收配置限制与解析问题的诊断集合。</param>
    private static void InspectProject(string path, string root, ImmutableArray<AnalysisFinding>.Builder findings)
    {
        try
        {
            XDocument document = XDocument.Load(path, LoadOptions.SetLineInfo);
            foreach (XElement element in document.Descendants().Where(element =>
                element.Attribute("Condition") is not null || element.Name.LocalName is "PackageReference" or "Compile" or "Import"))
            {
                int line = ((IXmlLineInfo)element).LineNumber;
                findings.Add(new("AT204", FindingSeverity.Unsupported,
                    "暂不求值 MSBuild 条件、外部包、导入或自定义编译项；使用源码视图，不冒充实际构建配置。",
                    new(Path.GetRelativePath(root, path).Replace('\\', '/'), line, line)));
            }
        }
        catch (Exception error) when (error is XmlException or IOException or UnauthorizedAccessException)
        {
            findings.Add(Unsupported("AT204", root, path, "项目配置暂不可解析：" + error.Message));
        }
    }

    /// <summary>把读取或配置限制转为包含相对文件位置的统一诊断。</summary>
    /// <param name="rule">限制对应的规则编号。</param>
    /// <param name="root">仓库根目录。</param>
    /// <param name="path">受影响的文件或目录。</param>
    /// <param name="message">具体原因与处理提示。</param>
    /// <returns>保留来源位置的未支持诊断。</returns>
    private static AnalysisFinding Unsupported(string rule, string root, string path, string message) =>
        new(rule, FindingSeverity.Unsupported, message, new(Path.GetRelativePath(root, path).Replace('\\', '/'), 1, 1));
}
