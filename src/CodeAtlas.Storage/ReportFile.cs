using System.Text.Json;
using System.Text.Json.Serialization;
using CodeAtlas.Contracts;

namespace CodeAtlas.Storage;

/// <summary>打开用户指定的报告文件；只读取 JSON，不执行其中的路径或代码。</summary>
public static class ReportFile
{
    private static readonly JsonSerializerOptions Options = new() { Converters = { new JsonStringEnumConverter() } };

    /// <summary>读取并校验报告结构；失败时不影响当前工作区。</summary>
    /// <param name="path">报告文件路径。</param>
    /// <param name="cancellationToken">取消读取信号。</param>
    /// <returns>结构完整且版本受支持的快照。</returns>
    public static async Task<AnalysisReport> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        AnalysisReport report = await JsonSerializer.DeserializeAsync<AnalysisReport>(stream, Options, cancellationToken)
            ?? throw new InvalidDataException("报告为空。");
        if (report.SchemaVersion != 1 || report.Graph is null || report.Coverage is null || report.Graph.Nodes.IsDefault ||
            report.Graph.Relations.IsDefault || report.Graph.Documents.IsDefault || report.Findings.IsDefault)
            throw new InvalidDataException("报告版本或结构不受支持；当前工作区保持不变。");
        return report;
    }
}
