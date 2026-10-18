using CodeAtlas.Contracts;
using CodeAtlas.Exploration;
using CodeAtlas.Framework;

namespace CodeAtlas.Desktop;

/// <summary>桌面组合边界：在后台载入或分析，在界面线程一次性交付索引。</summary>
[AtlasComponent("atlas.explore.workspace", ArchitecturalRole.Presentation)]
public sealed class DesktopWorkspace : ObservableObject, IDisposable
{
    private readonly IAnalysisService analysis;
    private readonly IReportRepository reports;
    private readonly IReportFileReader files;
    private CancellationTokenSource? operation;
    private bool busy;
    private string status = "打开分析报告，或选择源码文件夹开始分析。";
    private string? error;
    public ExplorerSession Session { get; } = new();
    public bool IsBusy { get => busy; private set => Set(ref busy, value); }
    public string Status { get => status; private set => Set(ref status, value); }
    public string? Error { get => error; private set => Set(ref error, value); }
    public string ReportName => Session.Index?.Report.Name ?? "尚未打开报告";
    public string Coverage => Session.Index is { } index ? $"{index.Report.Coverage.Files} 文件 · {index.Report.Coverage.Symbols} 符号 · {index.Report.Coverage.UnsupportedFiles} 文件暂不支持语义" : "本地桌面工作台";
    public bool HasReport => Session.Index is not null;
    public string FindingsLabel => Session.Index is { } index ? $"问题与限制 ({index.Report.Findings.Length})" : "问题与限制";
    public string SnapshotLabel => Session.Index is { } index ? $"源码快照 · {index.Report.AnalyzedAt.LocalDateTime:yyyy-MM-dd HH:mm} · 只读分析" : "选择源码仓库，生成可追溯的代码地图";

    /// <summary>为此工作区创建共享服务，不启动浏览器或 HTTP 服务。</summary>
    public DesktopWorkspace(IAnalysisService analysis, IReportRepository reports, IReportFileReader files)
    { this.analysis = analysis; this.reports = reports; this.files = files; }

    /// <summary>打开快照；失败或取消时保留原报告。</summary>
    public Task OpenAsync(string path) => RunAsync(async token =>
    {
        AnalysisReport report = await files.ReadAsync(path, token);
        return await Task.Run(() => new ReportIndex(report), token);
    }, "正在打开报告…");

    /// <summary>通过共享分析服务生成报告并保存，耗时工作不阻塞 UI 线程。</summary>
    public Task AnalyzeAsync(string root) => RunAsync(async token =>
    {
        string absolute = System.IO.Path.GetFullPath(root);
        string id = string.Equals(Session.Index?.Report.Root, absolute, StringComparison.OrdinalIgnoreCase) ? Session.Index!.Report.Id
            : "repo-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(absolute.ToUpperInvariant())))[..12].ToLowerInvariant();
        AnalysisReport report = await analysis.AnalyzeAsync(
            new(id, System.IO.Path.GetFileName(System.IO.Path.GetFullPath(root)), root), token);
        await reports.SaveAsync(report, token);
        return await Task.Run(() => new ReportIndex(report), token);
    }, "正在分析源码；可以取消…");

    /// <summary>请求取消当前加载或分析。</summary>
    public void Cancel() => operation?.Cancel();

    /// <summary>管理互斥操作和错误状态，只有成功结果替换当前索引。</summary>
    private async Task RunAsync(Func<CancellationToken, Task<ReportIndex>> action, string message)
    {
        if (IsBusy) throw new InvalidOperationException("已有操作正在运行，请先等待或取消。");
        using var source = new CancellationTokenSource(); operation = source;
        IsBusy = true; Error = null; Status = message;
        try
        {
            ReportIndex result = await Task.Run(() => action(source.Token), source.Token);
            source.Token.ThrowIfCancellationRequested();
            Session.Load(result); Changed(nameof(ReportName)); Changed(nameof(Coverage)); Changed(nameof(HasReport)); Changed(nameof(FindingsLabel)); Changed(nameof(SnapshotLabel));
            Status = "当前分析 · " + Coverage;
        }
        catch (OperationCanceledException) { Status = "操作已取消，原报告保持不变。"; }
        catch (Exception exception) { Error = exception.Message; Status = "操作失败：" + exception.Message; throw; }
        finally { operation = null; IsBusy = false; }
    }

    /// <summary>窗口关闭时取消当前工作并释放服务。</summary>
    public void Dispose() => operation?.Cancel();
}
