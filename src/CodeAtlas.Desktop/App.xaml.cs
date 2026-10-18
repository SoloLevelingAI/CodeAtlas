using System.Windows;
using CodeAtlas.Framework;
using CodeAtlas.Contracts;
using CodeAtlas.Runtime;
using Microsoft.Extensions.DependencyInjection;

[assembly: AtlasModule("atlas.explore.desktop", "WPF 桌面视图", ArchitecturalRole.Presentation)]

namespace CodeAtlas.Desktop;

/// <summary>本地 WPF 组合入口；启动失败时展示可理解的错误。</summary>
[AtlasComponent("atlas.runtime.host", ArchitecturalRole.Composition)]
public partial class App : System.Windows.Application
{
    private ServiceProvider? services;
    private DesktopControlServer? control;
    /// <summary>解析桌面启动选项，创建独立窗口与命名会话。</summary>
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            var options = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; i < e.Args.Length; i++)
            {
                string key = e.Args[i];
                if (key is not ("--report" or "--reports" or "--session") || ++i >= e.Args.Length || !options.TryAdd(key, e.Args[i]))
                    throw new ArgumentException("支持 --report <json>、--reports <folder>、--session <name>，不允许重复参数。");
            }
            string reports = options.GetValueOrDefault("--reports") ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodeAtlas", "reports");
            services = AtlasRuntime.Create(reports);
            var workspace = new DesktopWorkspace(services.GetRequiredService<IAnalysisService>(), services.GetRequiredService<IReportRepository>(), services.GetRequiredService<IReportFileReader>());
            var window = new MainWindow(workspace, options.GetValueOrDefault("--session") ?? "default");
            control = new DesktopControlServer(options.GetValueOrDefault("--session") ?? "default", window);
            MainWindow = window; window.Show();
            await window.StartAsync(options.GetValueOrDefault("--report"));
        }
        catch (Exception error)
        {
            MessageBox.Show(error.Message, "Code Atlas 启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    /// <summary>退出时停止当前用户控制端点并释放组合容器。</summary>
    protected override void OnExit(ExitEventArgs e)
    {
        control?.Dispose(); services?.Dispose(); base.OnExit(e);
    }
}
