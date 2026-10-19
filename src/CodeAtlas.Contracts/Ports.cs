using System.Collections.Immutable;
using CodeAtlas.Framework;

namespace CodeAtlas.Contracts;

/// <summary>源码盘点端口：提供原始文件和读取问题，不约定具体文件系统实现。</summary>
[AtlasComponent("atlas.analysis.inventory", ArchitecturalRole.Contract)]
public interface ISourceRepository
{
    /// <summary>读取仓库快照，保留源文件与读取限制。</summary>
    /// <param name="root">要盘点的仓库根目录。</param>
    /// <param name="cancellationToken">中止读取的信号。</param>
    /// <returns>源码原文、项目归属和读取诊断。</returns>
    [AtlasOperation(EffectKind.ExternalRead)]
    Task<SourceSnapshot> ReadAsync(string root, CancellationToken cancellationToken = default);
}

/// <summary>语义索引端口：把源码快照转换为符号、文档、功能目录与关系。</summary>
[AtlasComponent("atlas.analysis.csharp", ArchitecturalRole.Contract)]
public interface ICodeIndexer
{
    /// <summary>从快照解析可确认的功能、文档、符号和静态关系。</summary>
    /// <param name="snapshot">源文件和盘点阶段诊断。</param>
    /// <param name="cancellationToken">中止索引的信号。</param>
    /// <returns>保留证据和语义限制的源码图。</returns>
    SourceGraph Index(SourceSnapshot snapshot, CancellationToken cancellationToken = default);
}

/// <summary>质量检查端口：从已有事实中找出边界与组织问题，不负责读取源码。</summary>
[AtlasComponent("atlas.quality.boundaries", ArchitecturalRole.Contract)]
public interface IArchitectureRules
{
    /// <summary>检查已知事实是否满足职责边界和层级说明规范。</summary>
    /// <param name="graph">语义索引提供的源码事实。</param>
    /// <returns>可定位到元素或文件的质量问题。</returns>
    [AtlasOperation(EffectKind.Pure)]
    ImmutableArray<AnalysisFinding> Evaluate(SourceGraph graph);

    /// <summary>把目标模型与源码图对照。没有模型时只报告未知，不报告通过。</summary>
    /// <param name="graph">语义索引提供的源码事实。</param>
    /// <param name="target">已解析的目标架构；没有模型时为空。</param>
    /// <param name="problem">模型存在但无法读取时的原因。</param>
    /// <returns>漂移诊断；缺少模型时只有一条未知。</returns>
    [AtlasOperation(EffectKind.Pure)]
    ImmutableArray<AnalysisFinding> CompareTarget(SourceGraph graph, TargetArchitecture? target, string? problem = null);
}

/// <summary>截图导出端口：页面只提供已编码的 PNG，文件创建留在适配器。</summary>
[AtlasComponent("atlas.explore.desktop", ArchitecturalRole.Contract)]
public interface IScreenshotExport
{
    /// <summary>把 PNG 字节写到调用方给出的路径。</summary>
    /// <param name="path">以 .png 结尾的输出路径。</param>
    /// <param name="png">已经编码的 PNG。</param>
    /// <returns>写入后的绝对路径。</returns>
    string WritePng(string path, byte[] png);
}

/// <summary>规范原文端口：界面不直接构造文件或流读取器。</summary>
[AtlasComponent("atlas.quality.standards", ArchitecturalRole.Contract)]
public interface IStandardsReader
{
    /// <summary>读取与规范 ID 对应、随应用发布的 Markdown。</summary>
    /// <param name="id">例如 A07。</param>
    /// <returns>Markdown 原文。</returns>
    string ReadMarkdown(string id);
}

/// <summary>目标架构读取端口。找不到模型时返回空模型，调用方必须把它当成未知。</summary>
[AtlasComponent("atlas.analysis.inventory", ArchitecturalRole.Contract)]
public interface ITargetModelSource
{
    /// <summary>读取仓库自己的目标模型，或调用方显式指定的文件。</summary>
    /// <param name="repositoryRoot">被分析仓库的根目录。</param>
    /// <param name="explicitPath">显式模型路径；为空时按约定查找。</param>
    /// <returns>模型或读取问题。两者都空表示仓库没有提供模型。</returns>
    TargetModelLoad Read(string repositoryRoot, string? explicitPath);
}

/// <summary>分析任务端口：输入仓库与报告标识，输出一次完整分析结果。</summary>
[AtlasComponent("atlas.analysis.run", ArchitecturalRole.Contract)]
public interface IAnalysisService
{
    /// <summary>协调一次完整的读取、索引与质量检查。</summary>
    /// <param name="request">报告身份与仓库路径。</param>
    /// <param name="cancellationToken">中止分析的信号。</param>
    /// <returns>带覆盖信息与诊断的分析报告。</returns>
    Task<AnalysisReport> AnalyzeAsync(AnalysisRequest request, CancellationToken cancellationToken = default);
}

/// <summary>报告持久化端口：创建、查询、更新和删除快照，与图表交互解耦。</summary>
[AtlasComponent("atlas.reports.store", ArchitecturalRole.Contract)]
public interface IReportRepository
{
    /// <summary>创建或替换指定报告快照。</summary>
    /// <param name="report">要保存的完整报告。</param>
    /// <param name="cancellationToken">中止保存的信号。</param>
    /// <returns>保存完成的任务。</returns>
    Task SaveAsync(AnalysisReport report, CancellationToken cancellationToken = default);
    /// <summary>读取指定标识的报告。</summary>
    /// <param name="id">报告标识。</param>
    /// <param name="cancellationToken">中止读取的信号。</param>
    /// <returns>报告快照；不存在时为 null。</returns>
    Task<AnalysisReport?> LoadAsync(string id, CancellationToken cancellationToken = default);
    /// <summary>列出可用报告，供界面选择分析对象。</summary>
    /// <param name="cancellationToken">中止枚举的信号。</param>
    /// <returns>报告标识、名称与分析时间。</returns>
    Task<ImmutableArray<ReportSummary>> ListAsync(CancellationToken cancellationToken = default);
    /// <summary>删除指定报告快照。</summary>
    /// <param name="id">待删除的报告标识。</param>
    /// <param name="cancellationToken">中止删除的信号。</param>
    /// <returns>删除完成的任务。</returns>
    Task DeleteAsync(string id, CancellationToken cancellationToken = default);
}

// The UI depends on this replaceable workspace contract, not on an analyzer or CRUD implementation.
/// <summary>探索工作区端口：提供图表数据与动作，界面无需认识具体功能后端。</summary>
[AtlasComponent("atlas.explore.workspace", ArchitecturalRole.Contract)]
public interface IExplorerWorkspace
{
    /// <summary>为探索界面提供一份报告及可用动作。</summary>
    /// <param name="reportId">要打开的报告，null 由后端决定默认项。</param>
    /// <param name="cancellationToken">中止加载的信号。</param>
    /// <returns>当前报告、动作与报告切换列表。</returns>
    Task<ExplorerWorkspace> LoadAsync(string? reportId = null, CancellationToken cancellationToken = default);
    /// <summary>按后端定义执行动作，界面不需要理解该动作的业务实现。</summary>
    /// <param name="actionId">后端声明的动作标识。</param>
    /// <param name="reportId">动作作用的报告。</param>
    /// <param name="cancellationToken">中止执行的信号。</param>
    /// <returns>执行后可显示的新工作区。</returns>
    Task<ExplorerWorkspace> ExecuteAsync(string actionId, string reportId, CancellationToken cancellationToken = default);
}
