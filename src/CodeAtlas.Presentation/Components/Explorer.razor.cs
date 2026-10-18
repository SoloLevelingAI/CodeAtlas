using System.Collections.Immutable;
using System.Globalization;
using CodeAtlas.Contracts;
using CodeAtlas.Framework;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace CodeAtlas.Presentation.Components;

/// <summary>功能地图页：把模块关系、逐层查找、用途说明与源码证据放在一个保留上下文的窗口。</summary>
[AtlasComponent("atlas.explore.map", ArchitecturalRole.Presentation)]
public partial class Explorer : ComponentBase, IAsyncDisposable
{
    [Inject] public GraphExplorerViewModel Vm { get; set; } = default!;
    [Inject] public IJSRuntime Js { get; set; } = default!;
    [Parameter] public string? InitialReportId { get; set; }
    [Parameter] public string? InitialNodeId { get; set; }
    [Parameter] public string? InitialFile { get; set; }
    [Parameter] public int InitialLine { get; set; }
    [Parameter] public ResolvedProfile? RuntimeProfile { get; set; }
    private string? focusKey;
    private bool restorePending;
    private DotNetObjectReference<Explorer>? mapReference;
    private bool mapBound;
    private AnalysisReport? mapReport;
    private ImmutableArray<FeaturePlacement> placementCache = [];
    private ImmutableArray<FeatureConnection> connectionCache = [];
    private readonly HashSet<string> expandedRules = [];
    private readonly Dictionary<string, int> findingLimits = [];
    private ExpandableMap? expandable;
    private ExpandableMap? moduleTree;
    private bool runtimeMode;
    private string? selectedModuleId;
    private string? SelectedFeatureId => runtimeMode ? selectedModuleId : Vm.State.FeatureId;
    private ModuleDescriptor? SelectedModule => RuntimeProfile?.Modules.FirstOrDefault(module => module.Id == selectedModuleId);
    private ExpandableMap Tree => runtimeMode ? moduleTree ??= new(Vm, RuntimeProfile) : expandable ??= new(Vm);
    private ViewportState mapViewport = new();
    private bool showDependencies = true;
    private bool diagramMode;
    private bool showDirectory;
    private double CanvasWidth => Math.Max(620, Tree.Branches.Select(branch => branch.X + 280).DefaultIfEmpty(620).Max());
    private double CanvasHeight => Math.Max(600, Tree.Branches.Select(branch => branch.Y + 150).DefaultIfEmpty(600).Max());

    private AnalysisReport? Report => Vm.Workspace?.Report;
    private AnalysisFinding? Finding => Vm.State.FindingIndex is { } index ? Report?.Findings[index] : null;
    private CodeRelation? Relation => Vm.State.RelationIndex is { } index ? Report?.Graph.Relations[index] : null;
    private SourceLocation? Source => Finding?.Source ?? Relation?.Source ?? Vm.Selected?.Source
        ?? (Vm.State.FocusPath is { Length: > 0 } path ? new SourceLocation(path, Math.Max(1, Vm.State.FocusLine), Math.Max(1, Vm.State.FocusLine)) : null)
        ?? (Vm.State.Panel == "graph" ? Vm.CurrentFeature?.Source : null);
    private string Transform => FormattableString.Invariant($"translate({mapViewport.X} {mapViewport.Y}) scale({mapViewport.Zoom})");

    private ImmutableArray<FeaturePlacement> Placements { get { EnsureMap(); return placementCache; } }
    private string MiniViewBox => $"0 0 620 {Math.Max(200, (Vm.RootFeatures.Length + 2) / 3 * 90 + 20)}";
    private string MapViewBox => FormattableString.Invariant($"0 0 {CanvasWidth} {CanvasHeight}");
    private string PageTitle => Vm.State.Panel switch { "quality" => "边界与结构检查", "pending" => "补齐归属与说明", "unsupported" => "确认分析盲区", _ => Vm.CurrentClass is { } type ? type.Name + " · 成员" : Vm.CurrentFeature?.Name ?? "功能总览" };
    private string PagePurpose => Vm.State.Panel switch
    {
        "quality" => "检查全仓库的职责越界、层级错误和说明缺失；定位问题并查看修改建议。",
        "pending" => "整理未归属和说明不完整的代码；这里的缺口不会被地图隐藏。",
        "unsupported" => "查看当前分析器无法确认的语义与原始文件，明确报告的适用范围。",
        _ => Vm.CurrentFeature?.Purpose ?? "先选择要理解的功能，逐层找到子功能、页面、类型与方法。每一层都有明确任务。"
    };

    protected override async Task OnParametersSetAsync()
    {
        string key = (InitialReportId ?? "") + "|" + (InitialNodeId ?? "") + "|" + (InitialFile ?? "") + "|" + InitialLine;
        if (focusKey == key && Vm.Workspace is not null)
        {
            return;
        }
        if (Vm.Workspace is null || (InitialReportId is not null && Vm.Workspace.Report.Id != InitialReportId))
        {
            await Vm.LoadAsync(string.IsNullOrWhiteSpace(InitialReportId) ? null : InitialReportId);
        }
        if (Vm.Workspace is null)
        {
            return;
        }
        focusKey = key;
        if (!string.IsNullOrEmpty(InitialNodeId) || !string.IsNullOrEmpty(InitialFile))
        {
            Vm.FocusEvidence(string.IsNullOrEmpty(InitialNodeId) ? null : InitialNodeId, InitialFile, InitialLine);
        }
    }

    private string PageHref(string path) => Vm.Workspace is null ? path : path + "?report=" + Uri.EscapeDataString(Vm.Workspace.Report.Id);

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!mapBound && Report is not null)
        {
            mapReference ??= DotNetObjectReference.Create(this);
            await Js.InvokeVoidAsync("atlasView.bindMap", mapReference);
            mapBound = true;
        }
        if (restorePending)
        {
            restorePending = false;
            await Js.InvokeVoidAsync("atlasView.restore", Vm.State.Viewport);
        }
    }

    private async Task ChangeAsync(Action change)
    {
        if (!runtimeMode) await CaptureAsync();
        runtimeMode = false;
        change();
        restorePending = true;
    }

    private async Task CaptureAsync()
    {
        ScrollPosition scroll = await Js.InvokeAsync<ScrollPosition>("atlasView.capture");
        Vm.SetViewport((Vm.State.Viewport ?? new()) with { ListScroll = scroll.ListScroll, DetailScroll = scroll.DetailScroll });
    }

    private async Task ChangeReportAsync(ChangeEventArgs args)
    {
        await Vm.LoadAsync(args.Value?.ToString());
        mapViewport = new();
        expandedRules.Clear();
        findingLimits.Clear();
        restorePending = true;
    }

    private async Task ExecuteAsync(string id)
    {
        await CaptureAsync();
        await Vm.ExecuteAsync(id);
        restorePending = true;
    }

    private void Search(ChangeEventArgs args) { Vm.SetQuery(args.Value?.ToString() ?? ""); findingLimits.Clear(); if (!string.IsNullOrWhiteSpace(Vm.State.Query)) showDirectory = true; }
    private void Zoom(double amount) => mapViewport = mapViewport with { Zoom = Math.Clamp(mapViewport.Zoom + amount, .55, 1.8) };
    /// <summary>接收一次已在浏览器绘制完毕的拖动结果，记录到导航快照。</summary>
    /// <param name="x">SVG 坐标中的水平平移。</param>
    /// <param name="y">SVG 坐标中的垂直平移。</param>
    /// <returns>更新后的界面任务；鼠标移动过程中不执行此回调。</returns>
    [JSInvokable]
    public Task CommitMapPan(double x, double y)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y)) return Task.CompletedTask;
        mapViewport = mapViewport with { X = x, Y = y };
        return InvokeAsync(StateHasChanged);
    }

    /// <summary>释放浏览器手势监听器与回调引用，避免重连或离开页面后重复绑定。</summary>
    /// <returns>释放完成的任务。</returns>
    public async ValueTask DisposeAsync()
    {
        if (mapBound)
        {
            try { await Js.InvokeVoidAsync("atlasView.dispose"); }
            catch (JSDisconnectedException) { }
        }
        mapReference?.Dispose();
        GC.SuppressFinalize(this);
    }

    private void ToggleRule(string rule) { if (!expandedRules.Add(rule)) expandedRules.Remove(rule); }
    private void MoreFindings(string rule) => findingLimits[rule] = findingLimits.GetValueOrDefault(rule, 50) + 50;

    private Task OpenFeatureKey(KeyboardEventArgs args, string id) => args.Key is "Enter" or " " ? ChangeAsync(() => Vm.SetFeature(id)) : Task.CompletedTask;
    private Task SelectBranch(MapBranch branch)
    {
        if (runtimeMode) { selectedModuleId = branch.Node.FeatureId; return Task.CompletedTask; }
        return ChangeAsync(() =>
        {
            if (branch.Node.SymbolId is { } symbol)
            {
                if (Vm.State.FeatureId != branch.Feature) Vm.SetFeature(branch.Feature);
                Vm.Select(symbol);
            }
            else Vm.SetFeature(branch.Node.FeatureId);
        });
    }
    private Task SelectBranchKey(KeyboardEventArgs args, MapBranch branch) => args.Key is "Enter" or " " ? SelectBranch(branch) : Task.CompletedTask;
    private string BranchPath(string fromId, string toId)
    {
        MapBranch from = Tree.Branches.Single(branch => branch.Key == fromId);
        MapBranch to = Tree.Branches.Single(branch => branch.Key == toId);
        return Curve(from.X, from.Y, to.X, to.Y, 252, 92, string.CompareOrdinal(fromId, toId) < 0 ? 18 : -18);
    }

    /// <summary>按中英文近似显示宽度截取地图文字，完整说明保留在悬停提示与详情。</summary>
    /// <param name="value">原始名称或用途。</param>
    /// <param name="length">半角字符宽度预算。</param>
    /// <returns>适合节点宽度的文字片段。</returns>
    private static string ShortLabel(string value, int length)
    {
        int width = 0;
        for (int index = 0; index < value.Length; index++)
        {
            width += value[index] <= 127 ? 1 : 2;
            if (width > length) return value[..index] + "…";
        }
        return value;
    }

    private static string RuleName(string rule) => rule switch
    {
        "AT001" => "职责未声明", "AT010" => "功能未登记层级", "AT011" => "功能任务不完整",
        "AT012" => "用途说明缺失", "AT013" => "功能层级无效", "AT014" => "方法集合过于扁平",
        "AT015" => "页面输入输出不明", "AT016" => "文档格式错误", "AT017" => "输入输出未说明",
        "AT101" => "职责越层依赖", "AT102" => "核心包含外部作用", "AT103" => "纯计算声明冲突",
        "AT104" => "契约包含实现", "AT105" => "交互直接外部作用", "AT106" => "契约签名暴露实现",
        "AT107" => "用例直接外部作用", "AT108" => "注册缺少提供声明", "AT109" => "端口有多个提供方",
        "AT110" => "提供方未见注册", "AT111" => "模块标识未登记", "AT118" => "容器注册未走组合入口",
        "AT120" => "规则缺少反例", "AT121" => "规范映射不一致", "AT130" => "状态保存整图集合",
        "AT200" => "未支持的文件类型", "AT205" => "运行时目标不可确认",
        "AT206" => "调用目标不可确认", "AT207" => "源码语义不完整", _ => "声明或分析问题"
    };

    private IEnumerable<(AnalysisFinding Finding, int Index)> Findings() => Report?.Findings.Select((finding, index) => (finding, index))
        .Where(item => (Vm.State.Panel == "unsupported" ? item.finding.Severity == FindingSeverity.Unsupported :
            Vm.State.Panel == "pending" ? item.finding.Rule is "AT001" or "AT002" or "AT004" or "AT010" or "AT011" or "AT012" or "AT013" or "AT014" or "AT015" or "AT016" or "AT017" : item.finding.Severity is FindingSeverity.Error or FindingSeverity.Warning) &&
            (string.IsNullOrWhiteSpace(Vm.State.Query) || (item.finding.Message + item.finding.Source.Path + item.finding.Rule)
                .Contains(Vm.State.Query, StringComparison.OrdinalIgnoreCase))) ?? [];

    private ImmutableArray<FeatureConnection> Connections() { EnsureMap(); return connectionCache; }

    /// <summary>报告不变时复用模块布局和连线，点击、悬停与滚动不重新扫描所有关系。</summary>
    private void EnsureMap()
    {
        if (ReferenceEquals(mapReport, Report)) return;
        mapReport = Report;
        placementCache = Vm.RootFeatures.Select((feature, index) => new FeaturePlacement(feature, 12 + index % 3 * 205, 20 + index / 3 * 90)).ToImmutableArray();
        if (Report is null) { connectionCache = []; return; }
        HashSet<string> roots = placementCache.Select(placement => placement.Feature.Id).ToHashSet(StringComparer.Ordinal);
        Dictionary<string, CodeNode> nodes = Report.Graph.Nodes.ToDictionary(node => node.Id);
        connectionCache = Report.Graph.Relations.Select((relation, index) => (relation, index))
            .Where(item => nodes.ContainsKey(item.relation.From) && nodes.ContainsKey(item.relation.To))
            .Select(item => new { From = Vm.RootOf(nodes[item.relation.From].Feature), To = Vm.RootOf(nodes[item.relation.To].Feature), item.index })
            .Where(item => item.From != item.To && roots.Contains(item.From) && roots.Contains(item.To))
            .GroupBy(item => (item.From, item.To))
            .Select(group => new FeatureConnection(group.Key.From, group.Key.To, group.Count(), group.First().index)).ToImmutableArray();
    }

    private string EdgePath(FeatureConnection edge)
    {
        FeaturePlacement from = Placements.Single(placement => placement.Feature.Id == edge.From);
        FeaturePlacement to = Placements.Single(placement => placement.Feature.Id == edge.To);
        return Curve(from.X, from.Y, to.X, to.Y, 184, 62, string.CompareOrdinal(edge.From, edge.To) < 0 ? 12 : -12);
    }

    private static string Curve(double fromX, double fromY, double toX, double toY, double width, double height, double bend)
    {
        double ax = fromX + width / 2, ay = fromY + height / 2, bx = toX + width / 2, by = toY + height / 2;
        double cx = (ax + bx) / 2 + bend, cy = (ay + by) / 2;
        double start = Math.Min((width / 2 + 3) / Math.Max(Math.Abs(cx - ax), .01), (height / 2 + 3) / Math.Max(Math.Abs(cy - ay), .01));
        double end = Math.Min((width / 2 + 5) / Math.Max(Math.Abs(cx - bx), .01), (height / 2 + 5) / Math.Max(Math.Abs(cy - by), .01));
        return FormattableString.Invariant($"M{ax + (cx - ax) * start} {ay + (cy - ay) * start} Q{cx} {cy} {bx + (cx - bx) * end} {by + (cy - by) * end}");
    }

    private IEnumerable<(int Number, string Text)> SourceLines()
    {
        SourceDocument? document = Report?.Graph.Documents.FirstOrDefault(document => document.Path == Source?.Path);
        if (document is null || Source is null)
        {
            return [];
        }
        string[] lines = document.Text.Split('\n');
        int start = Vm.State.FullSource ? 0 : Math.Max(0, Source.Line - 4);
        int count = Vm.State.FullSource ? lines.Length : 25;
        return lines.Skip(start).Take(count).Select((text, index) => (start + index + 1, text));
    }

    private IEnumerable<CodeRelation> SelectedRelations() => Report?.Graph.Relations
        .Where(relation => relation.From == Vm.State.SelectedId || relation.To == Vm.State.SelectedId)
        .DistinctBy(relation => (relation.From, relation.To, relation.Kind)).Take(30) ?? [];

    private string NodeName(string id) => Vm.FindNode(id)?.Name ?? id;

    private static string RoleName(ArchitecturalRole? role) => role switch
    {
        ArchitecturalRole.Feature => "功能核心", ArchitecturalRole.Presentation => "交互 / ViewModel",
        ArchitecturalRole.Contract => "接口与数据契约", ArchitecturalRole.DataAccess => "数据访问 / CRUD",
        ArchitecturalRole.Integration => "外部系统适配", ArchitecturalRole.Application => "用例编排",
        ArchitecturalRole.Composition => "组合与注册", ArchitecturalRole.Framework => "声明框架",
        ArchitecturalRole.Test => "回归测试", _ => "未声明职责"
    };

    private static string RoleDescription(ArchitecturalRole role) => role switch
    {
        ArchitecturalRole.Feature => "规则与计算 · 不直接访问界面或存储",
        ArchitecturalRole.Presentation => "选择与交互状态 · 只认识工作区契约",
        ArchitecturalRole.Contract => "端口、输入与输出 · 不包含实现",
        ArchitecturalRole.DataAccess => "读取、保存、更新、删除",
        ArchitecturalRole.Integration => "语言与外部 API 的具体适配",
        ArchitecturalRole.Application => "通过端口协调一次用例",
        _ => "选择实现与生命周期 · 胶水层"
    };

    private static string KindName(RelationKind kind) => kind switch
    {
        RelationKind.Invocation => "静态调用", RelationKind.ProvidesContract => "声明提供契约",
        RelationKind.Registration => "组合根实际注册", _ => "类型引用"
    };

    private sealed record FeaturePlacement(FeatureDefinition Feature, double X, double Y);
    private sealed record FeatureConnection(string From, string To, int Count, int Index);
    private sealed record ScrollPosition(double ListScroll, double DetailScroll);
}
