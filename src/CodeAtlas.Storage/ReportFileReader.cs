using CodeAtlas.Contracts;
using CodeAtlas.Framework;

namespace CodeAtlas.Storage;

/// <summary>报告文件读取端口的 JSON 实现。</summary>
[AtlasProvides(typeof(IReportFileReader))]
public sealed class ReportFileReader : IReportFileReader
{
    /// <summary>读取并校验报告；不修改文件。</summary>
    /// <param name="path">快照路径。</param>
    /// <param name="cancellationToken">取消读取信号。</param>
    /// <returns>报告快照。</returns>
    public Task<AnalysisReport> ReadAsync(string path, CancellationToken cancellationToken = default) => ReportFile.LoadAsync(path, cancellationToken);
}
