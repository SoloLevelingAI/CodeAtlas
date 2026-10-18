using System.Collections.Immutable;
using CodeAtlas.Contracts;
using CodeAtlas.Framework;

namespace CodeAtlas.Analysis.Core;

/// <summary>一条规范在生成资源中的标识、自动检查说明和诊断映射。</summary>
/// <param name="Id">规范 ID，例如 A07。</param>
/// <param name="Automation">自动检查范围原文。</param>
/// <param name="FindingRules">映射的诊断编号。</param>
[AtlasComponent("atlas.quality.regression", ArchitecturalRole.Feature)]
public sealed record StandardMapping(string Id, string Automation, IReadOnlyList<string> FindingRules);

/// <summary>核对规则发射点、回归反例和规范映射；不在分析任意仓库时把缺席反例写成符合。</summary>
[AtlasComponent("atlas.quality.regression", ArchitecturalRole.Feature)]
public static class ConformanceCatalog
{
    /// <summary>依赖文件系统权限，回归环境不一定能稳定制造失败。</summary>
    public static readonly ImmutableArray<string> EnvironmentSensitive = ["AT201"];

    /// <summary>检查每条已实现诊断都有反例，且规范里的 AT 编号都能在源码中找到发射点。</summary>
    /// <param name="triggered">本轮回归实际出现过的诊断编号。</param>
    /// <param name="emitters">源码字符串中的 AT 编号，视为已实现的发射点。</param>
    /// <param name="standards">规范资源中的条目。</param>
    /// <returns>AT120 表示缺反例，AT121 表示映射和实现不一致；空结果只说明这份目录对齐。</returns>
    [AtlasOperation(EffectKind.Pure)]
    public static ImmutableArray<AnalysisFinding> Evaluate(IReadOnlySet<string> triggered, IReadOnlyCollection<string> emitters, IReadOnlyList<StandardMapping> standards)
    {
        var findings = ImmutableArray.CreateBuilder<AnalysisFinding>();
        var location = new SourceLocation("docs/architecture-standard.v1.json", 1, 1);
        var emitterSet = emitters.ToHashSet(StringComparer.Ordinal);
        var mapped = new HashSet<string>(StringComparer.Ordinal);
        foreach (string rule in emitters.Distinct(StringComparer.Ordinal).OrderBy(item => item, StringComparer.Ordinal))
        {
            if (EnvironmentSensitive.Contains(rule, StringComparer.Ordinal) || triggered.Contains(rule))
            {
                continue;
            }
            findings.Add(new("AT120", FindingSeverity.Error,
                $"规则 {rule} 在源码中有发射点，但本轮回归没有反例触发它。请补正反例；不能把未运行写成已覆盖。", location));
        }
        foreach (StandardMapping standard in standards)
        {
            bool claimsCheck = standard.Automation.Contains("部分自动", StringComparison.Ordinal) || standard.Automation.Contains("自动检查", StringComparison.Ordinal);
            if (standard.FindingRules.Count == 0 && claimsCheck)
            {
                findings.Add(new("AT121", FindingSeverity.Error,
                    $"规范 {standard.Id} 写了自动检查，但没有映射诊断。空映射不能显示成已检查。", location));
            }
            foreach (string rule in standard.FindingRules)
            {
                if (!rule.StartsWith("AT", StringComparison.Ordinal))
                {
                    continue;
                }
                mapped.Add(rule);
                if (!emitterSet.Contains(rule))
                {
                    findings.Add(new("AT121", FindingSeverity.Error,
                        $"规范 {standard.Id} 映射了 {rule}，但源码中没有该诊断的发射点。请实现它，或从映射中去掉未实现编号。", location));
                }
            }
        }
        foreach (string rule in emitterSet.OrderBy(item => item, StringComparer.Ordinal))
        {
            if (!mapped.Contains(rule))
            {
                findings.Add(new("AT121", FindingSeverity.Error,
                    $"规则 {rule} 已有发射点，但没有任何规范映射它。诊断会无处归入架构条目。", location));
            }
        }
        return findings.ToImmutable();
    }
}
