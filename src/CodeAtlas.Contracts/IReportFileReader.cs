using CodeAtlas.Framework;

namespace CodeAtlas.Contracts;

/// <summary>从用户指定的快照路径读取报告，不要求界面认识 JSON 存储实现。</summary>
[AtlasComponent("atlas.reports.store", ArchitecturalRole.Contract)]
public interface IReportFileReader
{
    /// <summary>读取报告文件并验证格式。</summary>
    /// <param name="path">用户选中的文件。</param>
    /// <param name="cancellationToken">取消读取信号。</param>
    /// <returns>可供索引的完整报告。</returns>
    Task<AnalysisReport> ReadAsync(string path, CancellationToken cancellationToken = default);
}
