using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeAtlas.Contracts;
using CodeAtlas.Framework;

namespace CodeAtlas.Storage;

/// <summary>将报告保存为本地 JSON 快照，并提供查询、替换与删除；失败写入不会覆盖原报告。</summary>
[AtlasProvides(typeof(IReportRepository))]
public sealed class JsonReportRepository(string directory) : IReportRepository
{
    /// <summary>存放报告快照的绝对目录，所有报告路径必须由此目录与安全标识组成。</summary>
    private readonly string root = Path.GetFullPath(directory);
    /// <summary>报告持久化格式：缩进 JSON，枚举保存为可读名称。</summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>先完整写入临时文件，再原子替换报告；写入中断时清理临时文件。</summary>
    /// <param name="report">包含源码和关系证据的报告快照。</param>
    /// <param name="cancellationToken">取消写入的信号。</param>
    /// <returns>保存完成；失败时抛出异常并保留已有报告。</returns>
    public async Task SaveAsync(AnalysisReport report, CancellationToken cancellationToken = default)
    {
        string destination = Resolve(report.Id);
        Directory.CreateDirectory(root);
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, report, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    /// <summary>按安全标识读取报告，校验版本和标识，损坏文件保持原样。</summary>
    /// <param name="id">仅含字母、数字、横线或下划线的报告标识。</param>
    /// <param name="cancellationToken">取消读取的信号。</param>
    /// <returns>报告对象；文件不存在时返回 null。</returns>
    public async Task<AnalysisReport?> LoadAsync(string id, CancellationToken cancellationToken = default)
    {
        string path = Resolve(id);
        if (!File.Exists(path))
        {
            return null;
        }
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        AnalysisReport? report = await JsonSerializer.DeserializeAsync<AnalysisReport>(stream, JsonOptions, cancellationToken);
        if (report is null || report.SchemaVersion != 1 || report.Id != id)
        {
            throw new InvalidDataException("报告版本或标识不匹配；未覆盖原始文件。");
        }
        return report;
    }

    /// <summary>读取已保存报告的简要信息，供工作区切换报告。</summary>
    /// <param name="cancellationToken">取消遍历的信号。</param>
    /// <returns>按文件标识排序的报告摘要。</returns>
    public async Task<ImmutableArray<ReportSummary>> ListAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(root))
        {
            return [];
        }
        var results = ImmutableArray.CreateBuilder<ReportSummary>();
        foreach (string path in Directory.GetFiles(root, "*.json").Order(StringComparer.Ordinal))
        {
            AnalysisReport? report = await LoadAsync(Path.GetFileNameWithoutExtension(path), cancellationToken);
            if (report is not null)
            {
                results.Add(new(report.Id, report.Name, report.AnalyzedAt));
            }
        }
        return results.ToImmutable();
    }

    /// <summary>删除指定快照；先校验标识，避免通过路径跳转删除其他文件。</summary>
    /// <param name="id">要删除的报告标识。</param>
    /// <param name="cancellationToken">取消删除的信号。</param>
    /// <returns>删除完成的任务。</returns>
    public Task DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        File.Delete(Resolve(id));
        return Task.CompletedTask;
    }

    /// <summary>校验报告标识并组成存储路径，拒绝路径跳转与不受支持的文件名。</summary>
    /// <param name="id">待访问的报告标识。</param>
    /// <returns>位于报告目录内的 JSON 文件绝对路径。</returns>
    private string Resolve(string id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 80 ||
            id.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
        {
            throw new ArgumentException("报告标识只能包含英文字母、数字、横线或下划线。", nameof(id));
        }
        return Path.Combine(root, id + ".json");
    }
}
