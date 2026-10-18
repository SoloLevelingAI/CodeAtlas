using CodeAtlas.Composition;
using CodeAtlas.Contracts;
using CodeAtlas.Presentation;
using CodeAtlas.Runtime;
using Microsoft.Extensions.DependencyInjection;

namespace CodeAtlas.Host;

/// <summary>Web 宿主只解释已知视图标识；模块不能注入任意控件或脚本。</summary>
public static class WebModules
{
    /// <summary>选择并接入真实服务；未知 Profile 在启动时失败。</summary>
    public static ResolvedProfile Configure(IServiceCollection services, string reports, string profileId)
    {
        ModuleRegistry registry = AtlasRuntime.CreateRegistry(reports)
            .Register(new("atlas.explore.map", "功能地图页", "用同一个 ExpandableMap 展开源码功能与实际启用的运行模块。", new(0, 2, 0), "atlas.explore",
                Requires: [new("atlas.workspace", typeof(IExplorerWorkspace), new(1, 0))],
                Views: [new("atlas.explore.map", "代码探索", "web.main", "atlas.explorer", "atlas.workspace")]),
                collection => collection.AddScoped<GraphExplorerViewModel>());
        ModuleProfile profile = profileId switch
        {
            "web" => new("web", ["atlas.explore.map", "atlas.explore.navigation.history", "atlas.explore.index"]),
            "headless" => AtlasRuntime.HeadlessProfile,
            _ => throw new NotSupportedException("未知 Profile：" + profileId + "；支持 web、headless。")
        };
        return registry.Apply(services, profile);
    }
}
