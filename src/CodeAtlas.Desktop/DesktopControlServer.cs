using System.IO;
using System.IO.Pipes;
using CodeAtlas.Automation;
using CodeAtlas.Framework;

namespace CodeAtlas.Desktop;

/// <summary>只允许当前用户连接的命名管道适配器；所有界面动作都派发到 WPF 线程。</summary>
[AtlasComponent("atlas.runtime.control", ArchitecturalRole.Composition)]
public sealed class DesktopControlServer : IDisposable
{
    private readonly CancellationTokenSource stopping = new();
    private readonly MainWindow window;
    private readonly string pipeName;
    private readonly Mutex singleSession;
    private readonly SemaphoreSlim slots = new(4);
    private readonly Task listener;

    /// <summary>独占一个命名会话，避免两扇窗口随机响应同一个 CLI。</summary>
    public DesktopControlServer(string session, MainWindow window)
    {
        this.window = window; pipeName = ControlProtocol.PipeName(session);
        singleSession = new Mutex(true, pipeName + ".owner", out bool created);
        if (!created) { singleSession.Dispose(); throw new InvalidOperationException("该桌面会话已运行，请使用不同的 --session 名称。"); }
        listener = Task.Run(ListenAsync);
    }

    /// <summary>接收有并发上限的本机连接；单个慢客户端不会堵住取消与状态查询。</summary>
    private async Task ListenAsync()
    {
        try
        {
            while (!stopping.IsCancellationRequested)
            {
                await slots.WaitAsync(stopping.Token);
                var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 4, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                try { await pipe.WaitForConnectionAsync(stopping.Token); }
                catch { await pipe.DisposeAsync(); slots.Release(); throw; }
                _ = HandleAsync(pipe);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { await window.Dispatcher.InvokeAsync(() => window.ReportControlFailure(error.Message)); }
    }

    /// <summary>校验协议版本并隔离每个请求的异常，错误不会终止监听器。</summary>
    private async Task HandleAsync(NamedPipeServerStream pipe)
    {
        await using (pipe)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
            timeout.CancelAfter(TimeSpan.FromMinutes(2));
            try
            {
                ControlRequest request = await ControlProtocol.ReadAsync<ControlRequest>(pipe, timeout.Token);
                if (request.Version != 1 || request.Arguments is null) throw new NotSupportedException("暂不支持此协议版本或请求格式。");
                object? result = await (await window.Dispatcher.InvokeAsync(() => window.ExecuteControlAsync(request))).ConfigureAwait(false);
                await ControlProtocol.WriteAsync(pipe, new ControlResponse { Ok = true, Result = result }, timeout.Token);
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                try { await ControlProtocol.WriteAsync(pipe, new ControlResponse { Ok = false, Error = error.Message }, timeout.Token); }
                catch (Exception writeError) when (writeError is IOException or OperationCanceledException or ObjectDisposedException) { }
            }
            finally { slots.Release(); }
        }
    }

    /// <summary>取消监听；当前会话独占权在创建它的 UI 线程上释放。</summary>
    public void Dispose()
    {
        stopping.Cancel(); singleSession.ReleaseMutex(); singleSession.Dispose();
        // Connection tasks observe cancellation; avoid blocking the dispatcher during shutdown.
        _ = listener;
    }
}
