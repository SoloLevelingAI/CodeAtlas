using System.Text.Json;
using CodeAtlas.Contracts;
using CodeAtlas.Framework;

namespace CodeAtlas.Exploration;

/// <summary>版本化规范的阅读模型；只解释现有诊断，不重新认证或修改被分析代码。</summary>
[AtlasComponent("atlas.quality.standards", ArchitecturalRole.Presentation)]
public sealed class ArchitectureGuide
{
    public string Version { get; init; } = "";
    public string Intent { get; init; } = "";
    public ArchitectureRule[] Rules { get; init; } = [];
    public string[] Migration { get; init; } = [];

    /// <summary>读取随应用发布的规范，GUI 与 CLI 使用同一份内容。</summary>
    public static ArchitectureGuide Load()
    {
        using Stream stream = typeof(ArchitectureGuide).Assembly.GetManifestResourceStream("CodeAtlas.ArchitectureStandard.json")
            ?? throw new InvalidOperationException("应用缺少架构规范资源。");
        return JsonSerializer.Deserialize<ArchitectureGuide>(stream) ?? throw new InvalidDataException("架构规范内容为空。");
    }

    /// <summary>读取与当前规范 ID 一一对应、随应用发布的 Markdown 原文。</summary>
    public static string ReadMarkdown(string id)
    {
        if (!Load().Rules.Any(r => r.Id == id)) throw new KeyNotFoundException("未知规范 ID：" + id);
        using Stream stream = typeof(ArchitectureGuide).Assembly.GetManifestResourceStream("CodeAtlas.Standards." + id + ".md")
            ?? throw new InvalidOperationException("缺少规范 Markdown：" + id);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>把真实诊断映射到规范；无证据与未实现检查都不能变成通过。</summary>
    /// <param name="report">带原始覆盖和限制的报告；为空时只展示规范。</param>
    /// <returns>每项规范的诊断数量、有限证据和保守判断。</returns>
    public ArchitectureAssessment[] Assess(AnalysisReport? report) => Rules.Select(rule =>
    {
        AnalysisFinding[] findings = report?.Findings.Where(f => rule.FindingRules.Contains(f.Rule, StringComparer.Ordinal)).ToArray() ?? [];
        bool unknown = report is null || report.Coverage.Symbols == 0 || report.Coverage.UnclassifiedSymbols > 0 || report.Findings.Any(f => f.Severity == FindingSeverity.Unsupported);
        bool problems = findings.Any(f => f.Severity is FindingSeverity.Error or FindingSeverity.Warning);
        string status = report is null ? "尚未分析仓库"
            : rule.FindingRules.Length == 0 ? "需要人工验证 / 自动检查未实现"
            : problems ? "存在问题，需逐项审阅"
            : rule.ExternalEvidence ? "需要独立回归证据，不能凭本次源码图判为通过"
            : unknown ? "证据不足，不能判为符合"
            : "未发现已知问题，仍需人工确认";
        return new ArchitectureAssessment
        {
            Rule = rule, EvidenceCount = findings.Length, Evidence = findings.Take(30).ToArray(), Status = status
        };
    }).ToArray();
}

/// <summary>一条规范的要求、理由、自动检查范围与迁移验收条件。</summary>
[AtlasComponent("atlas.quality.standards", ArchitecturalRole.Presentation)]
public sealed class ArchitectureRule
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string MarkdownPath { get; init; } = "";
    public string Requirement { get; init; } = "";
    public string Reason { get; init; } = "";
    public string Automation { get; init; } = "";
    public string[] FindingRules { get; init; } = [];
    /// <summary>为 true 时，本次源码图没有对应诊断也不能判为符合，证据在独立回归里。</summary>
    public bool ExternalEvidence { get; init; }
    public string Change { get; init; } = "";
    public string Acceptance { get; init; } = "";
}

/// <summary>仅针对已知证据的阅读结论；不代表全项目认证。</summary>
[AtlasComponent("atlas.quality.standards", ArchitecturalRole.Presentation)]
public sealed class ArchitectureAssessment
{
    public ArchitectureRule Rule { get; init; } = new();
    public string Status { get; init; } = "";
    public int EvidenceCount { get; init; }
    public AnalysisFinding[] Evidence { get; init; } = [];
    public string Label => Rule.Id + "  " + Rule.Title;
}
