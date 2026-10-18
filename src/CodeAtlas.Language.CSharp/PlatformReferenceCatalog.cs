using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace CodeAtlas.Language.CSharp;

/// <summary>从可信的 .NET 安装读取相同的平台引用，避免 GUI 和 CLI 因宿主加载程序集不同而给出不同语义。</summary>
internal static class PlatformReferenceCatalog
{
    /// <summary>优先选取当前运行时主版本的引用包；缺少 SDK 时使用同版本共享运行时，且不加载目标项目 DLL。</summary>
    /// <returns>按文件名去重、顺序稳定的元数据引用。</returns>
    public static ImmutableArray<MetadataReference> Load()
    {
        string runtime = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        string? installation = Directory.GetParent(runtime)?.Parent?.Parent?.FullName;
        var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AddDirectory(runtime, paths);
        if (installation is not null)
        {
            foreach (string framework in new[] { "Microsoft.NETCore.App", "Microsoft.AspNetCore.App", "Microsoft.WindowsDesktop.App" })
            {
                string pack = Path.Combine(installation, "packs", framework + ".Ref");
                string? version = LatestCompatible(pack);
                string? reference = version is null ? null : Path.Combine(version, "ref", "net" + Environment.Version.Major + ".0");
                if (reference is not null && Directory.Exists(reference))
                {
                    if (framework == "Microsoft.NETCore.App") paths.Clear();
                    AddDirectory(reference, paths);
                }
                else if (LatestCompatible(Path.Combine(installation, "shared", framework)) is { } shared) AddDirectory(shared, paths);
            }
        }
        // Analyzer APIs are a declared tool dependency, not arbitrary assemblies loaded by the host.
        foreach (string path in ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            if (Path.GetFileName(path) is "Microsoft.CodeAnalysis.dll" or "Microsoft.CodeAnalysis.CSharp.dll") paths[Path.GetFileName(path)] = path;
        return paths.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => (MetadataReference)MetadataReference.CreateFromFile(p.Value)).ToImmutableArray();
    }

    /// <summary>选择已安装且主版本匹配的最大稳定版本。</summary>
    private static string? LatestCompatible(string root)
    {
        if (!Directory.Exists(root)) return null;
        return Directory.GetDirectories(root).Select(path => (Path: path, Parsed: Version.TryParse(Path.GetFileName(path), out Version? v) ? v : null))
            .Where(item => item.Parsed?.Major == Environment.Version.Major).OrderByDescending(item => item.Parsed).Select(item => item.Path).FirstOrDefault();
    }

    /// <summary>从可信安装目录加入托管引用；更完整的引用包可以覆盖运行时占位程序集。</summary>
    private static void AddDirectory(string directory, Dictionary<string, string> result)
    {
        if (!Directory.Exists(directory)) return;
        foreach (string file in Directory.GetFiles(directory, "*.dll"))
        {
            try
            {
                _ = System.Reflection.AssemblyName.GetAssemblyName(file);
                result[Path.GetFileName(file)] = file;
            }
            catch (BadImageFormatException) { /* Native runtime libraries do not contain C# metadata. */ }
        }
    }
}
