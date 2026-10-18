using CodeAtlas.Analysis.Core;
using CodeAtlas.Application;
using CodeAtlas.Composition;
using CodeAtlas.Contracts;
using CodeAtlas.Framework;
using CodeAtlas.Language.CSharp;
using CodeAtlas.Storage;
using Microsoft.Extensions.DependencyInjection;

[assembly: AtlasModule("atlas.runtime.registration", "共享应用组装", ArchitecturalRole.Composition)]

namespace CodeAtlas.Runtime;

/// <summary>为桌面和命令行组装相同的端口提供方；不启动 Web 服务或载入被分析项目。</summary>
public static class AtlasRuntime
{
    /// <summary>创建独立服务容器；调用方负责释放，并决定报告存放位置。</summary>
    /// <param name="reports">报告目录。</param>
    /// <returns>可解析分析、存储与工作区端口的服务容器。</returns>
    public static ServiceProvider Create(string reports)
    {
        var services = new ServiceCollection();
        CreateRegistry(reports).Apply(services, DesktopProfile);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    /// <summary>桌面和旧 CLI 保留工作区端口；父模块与所需能力由解析器补齐。</summary>
    public static ModuleProfile DesktopProfile => new("desktop", ["atlas.explore.navigation.history", "atlas.explore.index"]);

    /// <summary>无界面分析只启用分析与报告模块，不注册探索工作区或视图。</summary>
    public static ModuleProfile HeadlessProfile => new("headless", ["atlas.analysis", "atlas.reports"]);

    /// <summary>登记内置实现；逻辑子模块无需各自拆成程序集。</summary>
    public static ModuleRegistry CreateRegistry(string reports) => new ModuleRegistry()
        .Register(new("atlas.reports", "报告管理", "读取和安全保存原始报告快照。", new(0, 2, 0),
            Provides: [new("atlas.reports", typeof(IReportRepository), new(1, 0)), new("atlas.report-reader", typeof(IReportFileReader), new(1, 0))],
            Dependencies: [new("atlas.analysis")]),
            services => services.AddAtlas<IReportRepository, JsonReportRepository>(_ => new JsonReportRepository(reports))
                .AddAtlas<IReportFileReader, ReportFileReader>())
        .Register(new("atlas.analysis", "源码分析", "通过现有分析流水线生成可追溯源码报告。", new(0, 2, 0),
            Provides: [new("atlas.analysis", typeof(IAnalysisService), new(1, 0)), new("atlas.sources", typeof(ISourceRepository), new(1, 0)),
                new("atlas.csharp", typeof(ICodeIndexer), new(1, 0)), new("atlas.rules", typeof(IArchitectureRules), new(1, 0)),
                new("atlas.target-model", typeof(ITargetModelSource), new(1, 0))]),
            services => services.AddAtlas<ISourceRepository, SourceRepository>().AddAtlas<ICodeIndexer, CSharpIndexer>()
                .AddAtlas<IArchitectureRules, ArchitectureRules>().AddAtlas<ITargetModelSource, TargetModelReader>()
                .AddAtlas<IAnalysisService, AnalyzeRepository>())
        .Register(new("atlas.explore", "代码探索", "通过可替换工作区连接已有报告、导航与嵌套地图。", new(0, 2, 0),
            Provides: [new("atlas.workspace", typeof(IExplorerWorkspace), new(1, 0))],
            Requires: [new("atlas.reports", typeof(IReportRepository), new(1, 0)), new("atlas.analysis", typeof(IAnalysisService), new(1, 0))],
            Dependencies: [new("atlas.analysis"), new("atlas.reports")]),
            services => services.AddAtlas<IExplorerWorkspace, ExplorerWorkspaceAdapter>())
        .Register(new("atlas.explore.navigation", "管理探索状态", "维护选择、路径和有界浏览历史。", new(0, 2, 0), "atlas.explore"))
        .Register(new("atlas.explore.navigation.history", "恢复浏览位置", "撤销和重做浏览动作，保留分支状态。", new(0, 2, 0), "atlas.explore.navigation"))
        .Register(new("atlas.explore.index", "报告索引", "复用现有 ReportIndex 查询原始报告，源码证据保留。", new(0, 2, 0), "atlas.explore"));
}
