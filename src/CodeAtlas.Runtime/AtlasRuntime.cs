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
        services.AddAtlas<IReportRepository, JsonReportRepository>(_ => new JsonReportRepository(reports))
            .AddAtlas<IReportFileReader, ReportFileReader>()
            .AddAtlas<ISourceRepository, SourceRepository>()
            .AddAtlas<ICodeIndexer, CSharpIndexer>()
            .AddAtlas<IArchitectureRules, ArchitectureRules>()
            .AddAtlas<IAnalysisService, AnalyzeRepository>()
            .AddAtlas<IExplorerWorkspace, ExplorerWorkspaceAdapter>();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }
}
