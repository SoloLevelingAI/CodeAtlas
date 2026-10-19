using System.Collections.Immutable;
using CodeAtlas.Contracts;
using CodeAtlas.Framework;

[assembly: AtlasModule("atlas.analysis.run", "分析用例与工作区适配", ArchitecturalRole.Application)]

namespace CodeAtlas.Application;

/// <summary>通过端口协调源码盘点、语义索引和质量检查，生成一次分析的完整报告。</summary>
[AtlasProvides(typeof(IAnalysisService))]
public sealed class AnalyzeRepository(ISourceRepository sources, ICodeIndexer indexer, IArchitectureRules rules, ITargetModelSource? targets = null) : IAnalysisService
{
    /// <summary>读取源码后构建关系图，执行职责与结构规则，并统计声明和支持覆盖范围。</summary>
    /// <param name="request">仓库路径以及报告的标识和名称。</param>
    /// <param name="cancellationToken">贯穿读取、索引和检查的取消信号。</param>
    /// <returns>包含源码证据、功能目录和问题清单的报告。</returns>
    public async Task<AnalysisReport> AnalyzeAsync(AnalysisRequest request, CancellationToken cancellationToken = default)
    {
        SourceSnapshot snapshot = await sources.ReadAsync(request.Root, cancellationToken);
        SourceGraph graph = await Task.Run(() => indexer.Index(snapshot, cancellationToken), cancellationToken);
        TargetModelLoad loaded = targets?.Read(snapshot.Root, request.TargetModelPath) ?? new(null, null);
        ImmutableArray<AnalysisFinding> findings = graph.Findings.AddRange(rules.Evaluate(graph))
            .AddRange(rules.CompareTarget(graph, loaded.Model, loaded.Problem));
        var coverage = new AnalysisCoverage(graph.Documents.Length,
            graph.Documents.Count(document => document.Language == ".cs"),
            graph.Documents.Count(document => document.Language != ".cs"), graph.Nodes.Length,
            graph.Nodes.Count(node => node.Role is not null), graph.Nodes.Count(node => node.Role is null),
            graph.Relations.Length);

        return new(1, request.Id, request.Name, snapshot.Root, DateTimeOffset.UtcNow, graph, coverage,
            findings.OrderBy(item => item.Source.Path, StringComparer.Ordinal).ThenBy(item => item.Source.Line)
                .ThenBy(item => item.Rule, StringComparer.Ordinal).ToImmutableArray(), Support, loaded.Model);
    }

    private static readonly ImmutableArray<SupportStatement> Support =
    [
        new("功能层级与页面任务", "支持", "AtlasFeature 的显式父级、用途、页面输入与完成结果；检查孤立父级、重复标识和循环。"),
        new("用途与输入输出说明", "部分支持", "读取源码 XML summary/param/returns；缺失处产生结构问题，不从函数名臆测含义；inheritdoc 暂不展开。"),
        new("C# 显式声明", "支持", "模块默认声明、类型/成员覆盖、作用声明和契约提供方；声明保留源码位置。"),
        new("静态引用与调用", "部分支持", "对源码视图中唯一解析的符号提供证据；接口调用只指向契约，不推断运行时目标。"),
        new("架构边界", "支持", "功能/交互/数据/适配/用例只能通过契约跨层连接；契约签名中的已知 UI/存储类型、交互和用例上的已知外部作用、以及可见的 AddAtlas 注册会另行报告。组合根和测试单独处理。看不到注册时不把模块拆装判成已通过。"),
        new("目标架构对照", "部分支持", "若仓库提供 LikeC4 投影的目标模型，则对照模块、依赖和 AtlasProvides。没有模型或模型读失败时记为未知，不记为通过。"),
        new("运行时分派与反射", "暂不支持", "动态、反射和委托目标逐项报告，不生成推测的确认连线。"),
        new("构建配置与生成代码", "暂不支持", "不执行被分析项目、不求值 MSBuild、不加载其程序集、不运行生成器；条件编译会明确报告。"),
        new("Razor / XAML / JS 等", "暂不支持", "保留原文与位置，计入未支持文件；C# 代码后置文件仍可独立分析。"),
        new("完整纯度证明", "暂不支持", "检测已知外部作用和显式作用契约；无发现不等于已证明纯函数。"),
        new("扫描边界", "明确排除", "不扫描 bin、obj、artifacts、.git、node_modules、packages、.vs；不跟随符号链接。")
    ];
}
