using System.Collections.Immutable;
using CodeAtlas.Contracts;
using CodeAtlas.Framework;

namespace CodeAtlas.Exploration;

/// <summary>按已保存报告里的功能与职责切分可认领切片；只引用现有诊断，不把缺少风险写成通过。</summary>
[AtlasComponent("atlas.quality.standards", ArchitecturalRole.Presentation)]
public static class DivisionBoard
{
    public const string Blocked = "交接受阻";
    public const string Clear = "未发现已知交接阻碍，仍需人工确认";
    public const string Hold = "存在分析盲区，不能凭空风险单独派工";
    public const string Claim = "可以按切片认领，仍需人工确认";
    public const string Pause = "先不要单独派工";
    public const string InboxName = "待整理";
    public const string NoReportBanner = "尚未分析仓库";
    public const string GapBanner = "证据不足。空的交接风险不能当成独立交付，也不能判为通过。";
    public const string BlindBanner = "报告含分析盲区。没有交接风险只表示未发现已知阻碍，不能判为通过。";
    public const string MixedBanner = "按功能内的职责切分。交接受阻的切片需要先处理越界证据；待整理项还不能单独认领。";
    public const string ClearBanner = "未发现已知交接阻碍，仍需人工确认。这不是认证通过。";

    /// <summary>交接风险只来自这些已经发射的规则，视图不再推断新的越界。</summary>
    public static readonly ImmutableArray<string> HandoffRules =
        ["AT101", "AT105", "AT106", "AT107", "AT108", "AT109", "AT110", "AT118", "AT130"];

    private static readonly ArchitecturalRole[] RoleOrder =
    [
        ArchitecturalRole.Contract, ArchitecturalRole.Feature, ArchitecturalRole.Presentation, ArchitecturalRole.Application,
        ArchitecturalRole.DataAccess, ArchitecturalRole.Integration, ArchitecturalRole.Composition, ArchitecturalRole.Framework, ArchitecturalRole.Test
    ];

    /// <summary>把一份报告读成分工：功能 × 职责切片、端口、交接风险和待整理。</summary>
    /// <param name="report">已保存的分析快照；为空时只说明尚未分析。</param>
    /// <returns>保守的认领建议。没有风险不等于通过。</returns>
    public static DivisionReading Read(AnalysisReport? report)
    {
        if (report is null)
        {
            return new(NoReportBanner, true, [], new(0, [], 0, [], 0, [], InboxName), "");
        }

        ImmutableArray<CodeNode> nodes = report.Graph.Nodes.IsDefault ? [] : report.Graph.Nodes;
        ImmutableArray<CodeRelation> relations = report.Graph.Relations.IsDefault ? [] : report.Graph.Relations;
        ImmutableArray<FeatureDefinition> features = report.Graph.Features.IsDefault ? [] : report.Graph.Features;
        ImmutableArray<AnalysisFinding> findings = report.Findings.IsDefault ? [] : report.Findings;
        Dictionary<string, FeatureDefinition> catalog = features
            .GroupBy(feature => feature.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        Dictionary<string, CodeNode> byId = nodes
            .GroupBy(node => node.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        bool evidenceGap = report.Coverage.Symbols == 0;
        bool unsupported = findings.Any(finding => finding.Severity == FindingSeverity.Unsupported);

        var slices = new Dictionary<(string Feature, ArchitecturalRole Role), List<CodeNode>>();
        var inboxSymbols = new List<CodeNode>();
        foreach (CodeNode node in byId.Values)
        {
            if (node.Role is { } role && catalog.ContainsKey(node.Feature))
            {
                var key = (node.Feature, role);
                if (!slices.TryGetValue(key, out List<CodeNode>? bucket))
                {
                    slices[key] = bucket = [];
                }
                bucket.Add(node);
            }
            else
            {
                inboxSymbols.Add(node);
            }
        }

        var risks = new Dictionary<(string Feature, ArchitecturalRole Role), List<DivisionRisk>>();
        var inboxFindings = new List<DivisionRisk>();
        var unplaced = new List<DivisionRisk>();
        foreach (AnalysisFinding finding in findings)
        {
            DivisionRisk risk = ToRisk(finding);
            if (finding.Rule is "AT001" or "AT010")
            {
                inboxFindings.Add(risk);
                continue;
            }
            if (!HandoffRules.Contains(finding.Rule, StringComparer.Ordinal))
            {
                continue;
            }
            if (finding.NodeId is { } id && byId.TryGetValue(id, out CodeNode? owner) && owner.Role is { } role && catalog.ContainsKey(owner.Feature))
            {
                var key = (owner.Feature, role);
                if (!risks.TryGetValue(key, out List<DivisionRisk>? bucket))
                {
                    risks[key] = bucket = [];
                }
                bucket.Add(risk);
            }
            else
            {
                unplaced.Add(risk);
            }
        }

        DivisionSlice[] built = slices.Select(pair => BuildSlice(pair.Key.Feature, pair.Key.Role, pair.Value, catalog, byId, relations,
            risks.GetValueOrDefault(pair.Key) ?? [], evidenceGap)).ToArray();
        built = built.OrderBy(slice => slice.FeaturePath, StringComparer.Ordinal)
            .ThenBy(slice => slice.FeatureId, StringComparer.Ordinal)
            .ThenBy(slice => Array.IndexOf(RoleOrder, slice.Role)).ToArray();
        bool blocked = built.Any(slice => slice.Status == Blocked) || unplaced.Count > 0;
        bool inbox = inboxSymbols.Count > 0 || inboxFindings.Count > 0;
        string banner = evidenceGap ? GapBanner
            : unsupported ? BlindBanner + (blocked || inbox ? "交接受阻和待整理项仍然有效。" : "")
            : blocked || inbox ? MixedBanner : ClearBanner;
        string[] loose = inboxSymbols.OrderBy(node => node.Name, StringComparer.Ordinal).ThenBy(node => node.Id, StringComparer.Ordinal)
            .Take(40).Select(node => node.Name + " · " + node.Feature).ToArray();
        return new(banner, evidenceGap, built.ToImmutableArray(), new(inboxSymbols.Count, loose.ToImmutableArray(),
            inboxFindings.Count, inboxFindings.Take(30).ToImmutableArray(), unplaced.Count, unplaced.Take(30).ToImmutableArray(), InboxName), report.Name);
    }

    /// <summary>把职责枚举显示成与代码地图一致的中文，不另造“适配”合并角色。</summary>
    /// <param name="role">源码中声明的职责。</param>
    /// <returns>界面用的职责名称。</returns>
    public static string RoleLabel(ArchitecturalRole role) => role switch
    {
        ArchitecturalRole.Feature => "功能核心",
        ArchitecturalRole.Presentation => "交互 / ViewModel",
        ArchitecturalRole.Contract => "接口与数据契约",
        ArchitecturalRole.DataAccess => "数据访问 / CRUD",
        ArchitecturalRole.Integration => "外部系统适配",
        ArchitecturalRole.Application => "用例编排",
        ArchitecturalRole.Composition => "组合与注册",
        ArchitecturalRole.Framework => "声明框架",
        ArchitecturalRole.Test => "回归测试",
        _ => "未声明职责"
    };

    private static DivisionSlice BuildSlice(string featureId, ArchitecturalRole role, List<CodeNode> members, Dictionary<string, FeatureDefinition> catalog,
        Dictionary<string, CodeNode> byId, ImmutableArray<CodeRelation> relations, List<DivisionRisk> risks, bool evidenceGap)
    {
        HashSet<string> memberIds = members.Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
        var ports = new List<DivisionPort>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (CodeNode member in members.Where(node => node.Role == ArchitecturalRole.Contract))
        {
            AddPort(ports, seen, "切片内契约", member.Id, member.Name, member.Symbol, member.Source);
        }
        foreach (CodeRelation relation in relations)
        {
            if (!memberIds.Contains(relation.From) || !byId.TryGetValue(relation.To, out CodeNode? target))
            {
                continue;
            }
            string? kind = relation.Kind switch
            {
                RelationKind.ProvidesContract => "提供声明",
                RelationKind.Registration => "实际注册",
                RelationKind.TypeReference or RelationKind.Invocation when target.Role == ArchitecturalRole.Contract => "依赖契约",
                _ => null
            };
            if (kind is not null)
            {
                AddPort(ports, seen, kind, target.Id, target.Name, target.Symbol, relation.Source);
            }
        }
        ports.Sort((left, right) => PortRank(left.Kind).CompareTo(PortRank(right.Kind)));
        CodeNode[] ordered = members.OrderBy(node => SymbolRank(node.Kind)).ThenBy(node => node.Name, StringComparer.Ordinal).ToArray();
        string assignment = risks.Count > 0 ? Pause : evidenceGap ? Hold : Claim;
        return new(featureId, catalog[featureId].Name, catalog[featureId].Purpose, FeaturePath(featureId, catalog), role, RoleLabel(role),
            members.Count, ordered.Take(8).Select(node => node.Name + " · " + node.Kind).ToImmutableArray(), ordered.FirstOrDefault()?.Id,
            ports.Count, ports.Take(24).ToImmutableArray(), risks.Count, risks.Take(30).ToImmutableArray(),
            risks.Count > 0 ? Blocked : Clear, assignment);
    }

    private static void AddPort(List<DivisionPort> ports, HashSet<string> seen, string kind, string nodeId, string name, string symbol, SourceLocation source)
    {
        if (seen.Add(kind + "\u001f" + nodeId))
        {
            ports.Add(new(kind, name, symbol, nodeId, source.Path, source.Line));
        }
    }

    private static string FeaturePath(string id, Dictionary<string, FeatureDefinition> catalog)
    {
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? current = id;
        while (current is not null && seen.Add(current) && catalog.TryGetValue(current, out FeatureDefinition? feature))
        {
            names.Insert(0, feature.Name);
            current = feature.ParentId;
        }
        return string.Join(" / ", names);
    }

    private static DivisionRisk ToRisk(AnalysisFinding finding) =>
        new(finding.Rule, finding.Severity.ToString(), finding.Message, finding.Source.Path, finding.Source.Line, finding.NodeId);

    private static int SymbolRank(string kind) => kind is "type" or "interface" or "class" or "record" or "struct" ? 0 : 1;
    private static int PortRank(string kind) => kind switch { "切片内契约" => 0, "提供声明" => 1, "实际注册" => 2, _ => 3 };
}

/// <summary>一次报告的分工阅读结果。</summary>
/// <param name="Banner">页面顶部的保守说明。</param>
/// <param name="EvidenceGap">没有任何已索引符号时为 true。未支持诊断只写在说明里，不把其余切片改判为失败。</param>
/// <param name="Slices">已登记功能中、职责已声明的切片。</param>
/// <param name="Inbox">不能单独认领的符号与未挂上的风险。</param>
/// <param name="ReportName">报告名称。</param>
[AtlasComponent("atlas.quality.standards", ArchitecturalRole.Presentation)]
public sealed record DivisionReading(string Banner, bool EvidenceGap, ImmutableArray<DivisionSlice> Slices, DivisionInbox Inbox, string ReportName)
{
    public int ClaimableCount => Slices.Count(slice => slice.Assignment == DivisionBoard.Claim);
    public int BlockedCount => Slices.Count(slice => slice.Status == DivisionBoard.Blocked);
}

/// <summary>一个功能内、同一职责的可认领切片。</summary>
[AtlasComponent("atlas.quality.standards", ArchitecturalRole.Presentation)]
public sealed record DivisionSlice(string FeatureId, string FeatureName, string Purpose, string FeaturePath, ArchitecturalRole Role, string RoleLabel,
    int NodeCount, ImmutableArray<string> Symbols, string? SampleNodeId, int PortCount, ImmutableArray<DivisionPort> Ports,
    int RiskCount, ImmutableArray<DivisionRisk> Risks, string Status, string Assignment);

/// <summary>切片使用或提供的契约，来自符号职责和已索引关系。</summary>
[AtlasComponent("atlas.quality.standards", ArchitecturalRole.Presentation)]
public sealed record DivisionPort(string Kind, string Name, string Symbol, string? NodeId, string Path, int Line);

/// <summary>一条已有诊断，保留规则、原文位置和可选符号。</summary>
[AtlasComponent("atlas.quality.standards", ArchitecturalRole.Presentation)]
public sealed record DivisionRisk(string Rule, string Severity, string Message, string Path, int Line, string? NodeId)
{
    public string Source => string.IsNullOrEmpty(Path) ? "" : Path + ":" + Line;
}

/// <summary>职责未声明、功能未登记，或交接风险对不上切片的代码。</summary>
[AtlasComponent("atlas.quality.standards", ArchitecturalRole.Presentation)]
public sealed record DivisionInbox(int SymbolCount, ImmutableArray<string> Symbols, int FindingCount, ImmutableArray<DivisionRisk> Findings,
    int UnplacedCount, ImmutableArray<DivisionRisk> UnplacedRisks, string Status);
