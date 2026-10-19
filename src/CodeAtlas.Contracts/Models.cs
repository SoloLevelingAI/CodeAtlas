using System.Collections.Immutable;
using CodeAtlas.Framework;

[assembly: AtlasModule("atlas.foundation.shared", "图表与操作契约", ArchitecturalRole.Contract)]

namespace CodeAtlas.Contracts;

public sealed record SourceLocation(string Path, int Line, int EndLine);
public sealed record SourceDocument(string Path, string Project, string Language, string Text);
/// <summary>一次源码盘点的结果，包含原文和未能读取的项目。</summary>
[AtlasComponent("atlas.analysis.inventory", ArchitecturalRole.Contract)]
public sealed record SourceSnapshot(string Root, ImmutableArray<SourceDocument> Documents,
    ImmutableArray<AnalysisFinding> InventoryFindings);

/// <summary>一个可定位的代码元素，携带功能、职责、用途、参数与返回说明。</summary>
[AtlasComponent("atlas.analysis.csharp", ArchitecturalRole.Contract)]
public sealed record CodeNode(string Id, string Name, string Symbol, string Kind, string? ParentId,
    string Feature, ArchitecturalRole? Role, EffectKind Effect, SourceLocation Source,
    SourceLocation? Declaration, string DeclarationBasis, bool HasBehavior = false,
    string Summary = "", string Inputs = "", string Outputs = "");

/// <summary>功能目录中的一个显式节点，描述父级、页面用途以及输入和结果。</summary>
[AtlasComponent("atlas.analysis.csharp", ArchitecturalRole.Contract)]
public sealed record FeatureDefinition(string Id, string Name, string Purpose, string? ParentId,
    FeatureKind Kind, string Inputs, string Outputs, SourceLocation Source);

public enum RelationKind { TypeReference, Invocation, ProvidesContract, Registration }
public sealed record CodeRelation(string From, string To, RelationKind Kind,
    SourceLocation Source, string Evidence);
public sealed record ObservedEffect(string NodeId, string Api, SourceLocation Source);
public enum FindingSeverity { Info, Warning, Error, Unsupported }
public sealed record AnalysisFinding(string Rule, FindingSeverity Severity, string Message,
    SourceLocation Source, string? NodeId = null);

/// <summary>索引阶段保留的事实集合；界面与质量规则共同读取此快照。</summary>
[AtlasComponent("atlas.analysis.csharp", ArchitecturalRole.Contract)]
public sealed record SourceGraph(ImmutableArray<SourceDocument> Documents,
    ImmutableArray<CodeNode> Nodes, ImmutableArray<CodeRelation> Relations,
    ImmutableArray<ObservedEffect> Effects, ImmutableArray<AnalysisFinding> Findings,
    ImmutableArray<FeatureDefinition> Features = default);

public sealed record AnalysisCoverage(int Files, int CSharpFiles, int UnsupportedFiles,
    int Symbols, int DeclaredSymbols, int UnclassifiedSymbols, int ResolvedRelations);
/// <summary>一次分析的可保存结果，包含功能图、诊断与支持范围。</summary>
[AtlasComponent("atlas.analysis.run", ArchitecturalRole.Contract)]
public sealed record AnalysisReport(int SchemaVersion, string Id, string Name, string Root,
    DateTimeOffset AnalyzedAt, SourceGraph Graph, AnalysisCoverage Coverage,
    ImmutableArray<AnalysisFinding> Findings, ImmutableArray<SupportStatement> Support,
    TargetArchitecture? Target = null);

public sealed record SupportStatement(string Capability, string Status, string Detail);
/// <summary>报告列表需要的名称、标识与时间。</summary>
[AtlasComponent("atlas.reports.store", ArchitecturalRole.Contract)]
public sealed record ReportSummary(string Id, string Name, DateTimeOffset AnalyzedAt);
/// <summary>指定要分析的仓库及生成报告的名称和标识。</summary>
[AtlasComponent("atlas.analysis.run", ArchitecturalRole.Contract)]
public sealed record AnalysisRequest(string Id, string Name, string Root, string? TargetModelPath = null);
/// <summary>后端提供的动作标识与界面名称，前端无需硬编码具体用例。</summary>
[AtlasComponent("atlas.explore.workspace", ArchitecturalRole.Contract)]
public sealed record WorkspaceAction(string Id, string Label);
/// <summary>探索界面当前使用的报告、可用动作和报告切换列表。</summary>
[AtlasComponent("atlas.explore.workspace", ArchitecturalRole.Contract)]
public sealed record ExplorerWorkspace(AnalysisReport Report, ImmutableArray<WorkspaceAction> Actions,
    ImmutableArray<ReportSummary> Reports);
public enum ExplorationPerspective { Feature, Interaction, Data }
