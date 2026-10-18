using System.Globalization;
using CodeAtlas.Contracts;
using CodeAtlas.Framework;

namespace CodeAtlas.Exploration;

/// <summary>把诊断定位回代码地图，或在报告原文里截取附近几行；不读取磁盘上的当前文件。</summary>
[AtlasComponent("atlas.quality.standards", ArchitecturalRole.Presentation)]
public static class EvidenceLocator
{
    /// <summary>生成代码地图地址。符号优先；没有符号时打开报告里的文件和行。两者都没有则返回 null。</summary>
    /// <param name="reportId">当前报告标识。</param>
    /// <param name="nodeId">诊断上的符号；可以没有。</param>
    /// <param name="path">诊断上的源文件路径。</param>
    /// <param name="line">诊断行号。</param>
    /// <param name="graph">报告中的图，用来确认符号和原文是否真的存在。</param>
    /// <returns>站内查询地址；无法定位时为 null。</returns>
    public static string? MapHref(string reportId, string? nodeId, string path, int line, SourceGraph graph)
    {
        var parts = new List<string> { "report=" + Uri.EscapeDataString(reportId) };
        if (nodeId is not null && graph.Nodes.Any(node => node.Id == nodeId))
        {
            parts.Add("node=" + Uri.EscapeDataString(nodeId));
        }
        else if (graph.Documents.Any(document => string.Equals(document.Path, path, StringComparison.Ordinal)))
        {
            parts.Add("file=" + Uri.EscapeDataString(path));
            parts.Add("line=" + line.ToString(CultureInfo.InvariantCulture));
        }
        else
        {
            return null;
        }
        return "/?" + string.Join('&', parts);
    }

    /// <summary>从报告快照截取诊断行附近的原文。</summary>
    /// <param name="graph">含原文的图。</param>
    /// <param name="path">文件路径，须与快照一致。</param>
    /// <param name="line">从 1 开始的行号。</param>
    /// <returns>最多五行；文件不在快照中时为空。</returns>
    public static IReadOnlyList<(int Number, string Text)> Excerpt(SourceGraph graph, string path, int line)
    {
        SourceDocument? document = graph.Documents.FirstOrDefault(item => string.Equals(item.Path, path, StringComparison.Ordinal));
        if (document is null || line <= 0)
        {
            return [];
        }
        string[] lines = document.Text.Split('\n');
        int start = Math.Max(0, line - 3);
        return lines.Skip(start).Take(5).Select((text, index) => (start + index + 1, text.Length > 220 ? text[..220] : text)).ToArray();
    }
}
