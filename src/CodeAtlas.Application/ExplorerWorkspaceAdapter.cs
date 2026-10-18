using CodeAtlas.Contracts;
using CodeAtlas.Framework;

namespace CodeAtlas.Application;

/// <summary>为图表界面接入报告与分析用例，对外只暴露工作区数据和动作。</summary>
[AtlasComponent("atlas.explore.workspace", ArchitecturalRole.Application)]
[AtlasProvides(typeof(IExplorerWorkspace))]
public sealed class ExplorerWorkspaceAdapter(IReportRepository reports, IAnalysisService analysis) : IExplorerWorkspace
{
    /// <summary>选择已保存报告并提供当前允许执行的工作区动作。</summary>
    /// <param name="reportId">要打开的报告；为空时使用列表中的第一份。</param>
    /// <param name="cancellationToken">取消加载的信号。</param>
    /// <returns>报告、可用动作与其他可切换报告。</returns>
    public async Task<ExplorerWorkspace> LoadAsync(string? reportId = null, CancellationToken cancellationToken = default)
    {
        var available = await reports.ListAsync(cancellationToken);
        string id = reportId ?? available.FirstOrDefault()?.Id ?? throw new InvalidOperationException("尚无分析报告，请先运行 analyze 命令。");
        AnalysisReport report = await reports.LoadAsync(id, cancellationToken)
            ?? throw new KeyNotFoundException("报告不存在：" + id);
        return new(report, [new("reanalyze", "重新分析当前源码")], available);
    }

    /// <summary>执行重新分析动作，保存新的源码快照，再更新工作区；拒绝未支持的动作。</summary>
    /// <param name="actionId">后端提供的动作标识，当前支持 reanalyze。</param>
    /// <param name="reportId">动作所作用的报告标识。</param>
    /// <param name="cancellationToken">取消执行的信号。</param>
    /// <returns>执行后的工作区。</returns>
    public async Task<ExplorerWorkspace> ExecuteAsync(string actionId, string reportId, CancellationToken cancellationToken = default)
    {
        if (actionId != "reanalyze")
        {
            throw new NotSupportedException("暂不支持的工作区指令：" + actionId);
        }
        AnalysisReport prior = await reports.LoadAsync(reportId, cancellationToken)
            ?? throw new KeyNotFoundException("报告不存在：" + reportId);
        AnalysisReport updated = await analysis.AnalyzeAsync(new(prior.Id, prior.Name, prior.Root), cancellationToken);
        await reports.SaveAsync(updated, cancellationToken);
        return await LoadAsync(reportId, cancellationToken);
    }
}
