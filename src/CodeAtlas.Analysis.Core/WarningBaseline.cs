using System.Text.Json;
using CodeAtlas.Contracts;

namespace CodeAtlas.Analysis.Core;

/// <summary>比较本次警告与已提交的基线。新出现或变多的警告失败；减少不视为失败。</summary>
public static class WarningBaseline
{
    /// <summary>返回超出基线的说明。空列表表示没有新增警告。</summary>
    public static IReadOnlyList<string> Compare(AnalysisReport report, string baselineJson)
    {
        using JsonDocument document = JsonDocument.Parse(baselineJson);
        var allowed = document.RootElement.GetProperty("warnings").EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value.GetInt32(), StringComparer.Ordinal);
        var problems = new List<string>();
        foreach (IGrouping<string, AnalysisFinding> group in report.Findings.Where(finding => finding.Severity == FindingSeverity.Warning)
            .GroupBy(finding => finding.Rule, StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            if (!allowed.TryGetValue(group.Key, out int limit))
            {
                problems.Add($"{group.Key} 不在警告基线中，本次有 {group.Count()} 条。");
            }
            else if (group.Count() > limit)
            {
                problems.Add($"{group.Key} 有 {group.Count()} 条警告，基线允许 {limit} 条。");
            }
        }
        return problems;
    }
}
