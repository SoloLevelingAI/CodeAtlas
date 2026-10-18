using System.Collections.Immutable;
using System.Text;
using System.Text.RegularExpressions;
using CodeAtlas.Contracts;
using CodeAtlas.Framework;

namespace CodeAtlas.Analysis.Core;

/// <summary>从已索引的签名、外部作用和注册关系提取可定位证据；看不到组合根或性能数据时不补成通过。</summary>
[AtlasComponent("atlas.quality.boundaries", ArchitecturalRole.Feature)]
public static class BoundaryEvidenceRules
{
    // 更具体的类型放前面，消息才不会停在更短的前缀上。命名空间前缀以点结尾。
    private static readonly string[] ForbiddenSignatureTypes =
    [
        "System.Windows.Controls.",
        "System.Windows.Forms.",
        "System.Windows.Media.",
        "Microsoft.UI.Xaml.",
        "System.Web.UI.",
        "System.Windows.Window",
        "System.Windows.FrameworkElement",
        "System.Windows.DependencyObject",
        "System.IO.FileStream",
        "System.IO.FileInfo",
        "System.IO.DirectoryInfo",
        "System.IO.StreamReader",
        "System.IO.StreamWriter",
        "System.IO.File",
        "System.IO.Directory",
        "System.Net.Http.HttpClient",
        "System.Net.Sockets.Socket",
        "System.Net.Sockets.TcpClient",
        "System.Diagnostics.Process",
        "System.Data.Common.DbConnection",
        "System.Data.Common.DbCommand",
        "System.Data.IDbConnection",
        "System.Data.IDbCommand",
        "Microsoft.EntityFrameworkCore.DbContext",
        "Microsoft.Data.SqlClient.SqlConnection",
        "Microsoft.Data.Sqlite.SqliteConnection",
        "System.Data.SqlClient.SqlConnection"
    ];

    private static readonly Regex SnapshotCollection = new(
        @"(?<![\p{L}\p{Nd}_])(?:List|IList|ICollection|IEnumerable|IReadOnlyList|IReadOnlyCollection|ISet|HashSet|SortedSet|Collection|ImmutableArray|ImmutableList|ImmutableHashSet)<(?:[^<>]*\.)?(?:SourceGraph|AnalysisReport)>|(?<![\p{L}\p{Nd}_])(?:SourceGraph|AnalysisReport)\[\]",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>检查契约签名、交互/用例外部作用、可见注册和整图集合字段。</summary>
    /// <param name="graph">语义索引留下的符号、关系与已知外部作用。</param>
    /// <returns>有源位置的新证据；缺少组合根时不把未注册报成违规。</returns>
    [AtlasOperation(EffectKind.Pure)]
    public static ImmutableArray<AnalysisFinding> Evaluate(SourceGraph graph)
    {
        var findings = ImmutableArray.CreateBuilder<AnalysisFinding>();
        var nodes = graph.Nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        foreach (CodeNode node in graph.Nodes)
        {
            if (node.Role == ArchitecturalRole.Contract && ReferencesForbidden(SignatureText(node), out string forbidden))
            {
                findings.Add(new("AT106", FindingSeverity.Error,
                    $"契约 {node.Name} 的签名暴露了 {forbidden}。请改用表达意图的类型，不要把 UI 控件、存储会话或具体适配器当作交换对象。",
                    node.Source, node.Id));
            }
            if (node.Role == ArchitecturalRole.Presentation && node.Kind is "field" or "property" && SnapshotCollection.IsMatch(node.Symbol))
            {
                findings.Add(new("AT130", FindingSeverity.Warning,
                    $"交互状态 {node.Name} 保存了 SourceGraph 或 AnalysisReport 的集合。历史应记录可逆的小变更；此检查不能证明浏览性能。",
                    node.Source, node.Id));
            }
        }

        foreach (ObservedEffect effect in graph.Effects)
        {
            if (!nodes.TryGetValue(effect.NodeId, out CodeNode? node) || node.Role is null || node.Effect == EffectKind.Pure)
            {
                continue;
            }
            if (node.Role == ArchitecturalRole.Presentation)
            {
                findings.Add(new("AT105", FindingSeverity.Warning,
                    $"交互代码直接调用外部作用 {effect.Api}。页面应通过用例契约接入，不要在 ViewModel 中直接做文件、网络、时钟或进程操作。",
                    effect.Source, node.Id));
            }
            else if (node.Role == ArchitecturalRole.Application && IsAdapterBypass(effect.Api))
            {
                findings.Add(new("AT107", FindingSeverity.Error,
                    $"用例编排直接调用外部作用 {effect.Api}。请把文件、网络、数据库和进程留在适配器，通过端口接入。",
                    effect.Source, node.Id));
            }
        }

        // 测试程序集里的注册是反例本身，不把它算进产品组合，也不因此把它写成未注册。
        bool ProductProvider(string nodeId) => !nodes.TryGetValue(nodeId, out CodeNode? node) || node.Role is not ArchitecturalRole.Test;
        CodeRelation[] registrations = graph.Relations.Where(relation => relation.Kind == RelationKind.Registration && ProductProvider(relation.From)).ToArray();
        CodeRelation[] provides = graph.Relations.Where(relation => relation.Kind == RelationKind.ProvidesContract && ProductProvider(relation.From)).ToArray();
        foreach (CodeRelation registration in registrations)
        {
            if (!provides.Any(item => item.From == registration.From && item.To == registration.To))
            {
                string provider = nodes.TryGetValue(registration.From, out CodeNode? from) ? from.Name : registration.From;
                string port = nodes.TryGetValue(registration.To, out CodeNode? to) ? to.Name : registration.To;
                findings.Add(new("AT108", FindingSeverity.Error,
                    $"{provider} 已注册为 {port}，但没有对应的 AtlasProvides。请在实现上显式声明它提供的接口。",
                    registration.Source, registration.From));
            }
        }
        foreach (IGrouping<string, CodeRelation> port in registrations.GroupBy(relation => relation.To, StringComparer.Ordinal))
        {
            string[] providers = port.Select(relation => relation.From).Distinct(StringComparer.Ordinal).ToArray();
            if (providers.Length < 2)
            {
                continue;
            }
            string portName = nodes.TryGetValue(port.Key, out CodeNode? contract) ? contract.Name : port.Key;
            foreach (CodeRelation registration in port.Where(relation => relation.From != providers[0]))
            {
                string provider = nodes.TryGetValue(registration.From, out CodeNode? from) ? from.Name : registration.From;
                findings.Add(new("AT109", FindingSeverity.Error,
                    $"端口 {portName} 有多个提供方（包括 {provider}）。同一契约只保留一个注册实现。",
                    registration.Source, registration.From));
            }
        }
        // 完全看不到 AddAtlas 时，不把“未注册”写成违规，也不把它写成已经接线。
        if (registrations.Length > 0)
        {
            foreach (CodeRelation provide in provides)
            {
                if (registrations.Any(item => item.From == provide.From && item.To == provide.To))
                {
                    continue;
                }
                string provider = nodes.TryGetValue(provide.From, out CodeNode? from) ? from.Name : provide.From;
                string port = nodes.TryGetValue(provide.To, out CodeNode? to) ? to.Name : provide.To;
                findings.Add(new("AT110", FindingSeverity.Error,
                    $"{provider} 声明提供 {port}，但可见的 AddAtlas 注册里没有它。请在组合根注册，或去掉未使用的提供方声明。",
                    provide.Source, provide.From));
            }
        }
        return findings.ToImmutable();
    }

    /// <summary>用例层要让出的是文件、网络、数据库和进程；时钟戳本身不是适配器替换点。</summary>
    private static bool IsAdapterBypass(string api) =>
        api.StartsWith("System.IO.", StringComparison.Ordinal) ||
        api.StartsWith("System.Net.", StringComparison.Ordinal) ||
        api.StartsWith("System.Diagnostics.Process.", StringComparison.Ordinal) ||
        api.StartsWith("System.Data.", StringComparison.Ordinal) ||
        api.StartsWith("Microsoft.Data.", StringComparison.Ordinal) ||
        api.StartsWith("Microsoft.EntityFrameworkCore.", StringComparison.Ordinal);

    /// <summary>拼出签名中的类型名，不包含 XML 说明正文，避免文档里提到某个类型就被误报。</summary>
    private static string SignatureText(CodeNode node)
    {
        var text = new StringBuilder(node.Symbol);
        foreach (string line in node.Inputs.Split('\n'))
        {
            int colon = line.IndexOf(" : ", StringComparison.Ordinal);
            int dash = line.IndexOf(" — ", StringComparison.Ordinal);
            if (colon >= 0 && dash > colon)
            {
                text.Append(' ').Append(line, colon + 3, dash - colon - 3);
            }
        }
        int outputDash = node.Outputs.IndexOf(" — ", StringComparison.Ordinal);
        if (outputDash > 0)
        {
            text.Append(' ').Append(node.Outputs, 0, outputDash);
        }
        return text.ToString();
    }

    /// <summary>判断显示签名是否含已知 UI、存储会话或具体适配器类型。</summary>
    /// <param name="text">符号、参数和返回类型的显示文本。</param>
    /// <param name="match">命中的类型或命名空间前缀。</param>
    /// <returns>命中禁用类型时为 true。</returns>
    [AtlasOperation(EffectKind.Pure)]
    public static bool ReferencesForbidden(string text, out string match)
    {
        foreach (string token in ForbiddenSignatureTypes)
        {
            int start = 0;
            while ((start = text.IndexOf(token, start, StringComparison.Ordinal)) >= 0)
            {
                if (token.EndsWith('.'))
                {
                    match = token.TrimEnd('.');
                    return true;
                }
                int end = start + token.Length;
                bool left = start == 0 || !char.IsLetterOrDigit(text[start - 1]);
                bool right = end >= text.Length || !char.IsLetterOrDigit(text[end]);
                if (left && right)
                {
                    match = token;
                    return true;
                }
                start = end;
            }
        }
        match = "";
        return false;
    }
}
