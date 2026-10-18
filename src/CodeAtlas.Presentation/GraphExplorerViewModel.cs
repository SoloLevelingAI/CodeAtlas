using System.Collections.Immutable;
using CodeAtlas.Contracts;
using CodeAtlas.Framework;

[assembly: AtlasModule("atlas.explore.navigation", "通用图表交互", ArchitecturalRole.Presentation)]

namespace CodeAtlas.Presentation;

public sealed record ViewportState(double X = 0, double Y = 0, double Zoom = 1,
    double ListScroll = 0, double DetailScroll = 0);
public sealed record ExplorerState(string? SelectedId = null, string Query = "",
    ArchitecturalRole? Scope = null, ExplorationPerspective Perspective = ExplorationPerspective.Feature,
    string Panel = "graph", int? FindingIndex = null, ViewportState? Viewport = null,
    int? RelationIndex = null, bool FullSource = false, string? FeatureId = null, string? ClassId = null, int Page = 0, int MapPage = 0,
    string? FocusPath = null, int FocusLine = 0);

/// <summary>管理功能路径、筛选和返回记录，通过工作区契约派发动作，不依赖具体功能实现。</summary>
public sealed partial class GraphExplorerViewModel(IExplorerWorkspace workspace)
{
    private readonly List<ExplorerState> history = [];
    private int historyIndex = -1;
    private readonly HashSet<string> pins = new(StringComparer.Ordinal);
    private readonly Dictionary<(string? Feature, string? Class), ViewportState> mapPositions = [];

    public ExplorerWorkspace? Workspace { get; private set; }
    public ExplorerState State { get; private set; } = new(Viewport: new());
    public bool IsBusy { get; private set; }
    public string? Error { get; private set; }
    public bool CanBack => historyIndex > 0;
    public bool CanForward => historyIndex >= 0 && historyIndex < history.Count - 1;
    public IReadOnlySet<string> Pins => pins;
    public CodeNode? Selected => FindNode(State.SelectedId);
    public ArchitecturalRole FocusRole => State.Perspective switch
    {
        ExplorationPerspective.Interaction => ArchitecturalRole.Presentation,
        ExplorationPerspective.Data => ArchitecturalRole.DataAccess,
        _ => ArchitecturalRole.Feature
    };

    /// <summary>通过工作区端口打开报告，建立新的浏览历史并移除无效固定项。</summary>
    /// <param name="id">报告标识；为空时由后端选择默认报告。</param>
    /// <param name="cancellationToken">中止加载的信号。</param>
    /// <returns>完成加载；错误以界面可读状态保留。</returns>
    [AtlasComponent("atlas.explore.workspace", ArchitecturalRole.Presentation)]
    public async Task LoadAsync(string? id = null, CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        Error = null;
        try
        {
            Workspace = await workspace.LoadAsync(id, cancellationToken);
            State = new(Viewport: new());
            history.Clear();
            mapPositions.Clear();
            history.Add(State);
            historyIndex = 0;
            pins.RemoveWhere(pin => !Workspace.Report.Graph.Nodes.Any(node => node.Id == pin));
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Error = error.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>派发后端声明的动作，刷新工作区并清除已过期的诊断位置，不判断动作的业务内容。</summary>
    /// <param name="actionId">后端提供的动作标识。</param>
    /// <param name="cancellationToken">中止执行的信号。</param>
    /// <returns>动作完成后更新的界面状态；失败时保留当前工作区。</returns>
    [AtlasComponent("atlas.explore.workspace", ArchitecturalRole.Presentation)]
    public async Task ExecuteAsync(string actionId, CancellationToken cancellationToken = default)
    {
        if (Workspace is null || IsBusy)
        {
            return;
        }
        IsBusy = true;
        Error = null;
        try
        {
            ExplorerWorkspace updated = await workspace.ExecuteAsync(actionId, Workspace.Report.Id, cancellationToken);
            Workspace = updated;
            pins.RemoveWhere(pin => !updated.Report.Graph.Nodes.Any(node => node.Id == pin));
            if (Selected is null)
            {
                State = State with { SelectedId = null, FindingIndex = null };
            }
            // Source findings are positional in a report, so stale indices must never survive refresh.
            State = State with { FindingIndex = null, RelationIndex = null };
            history.Clear();
            history.Add(State);
            historyIndex = 0;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Error = error.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>返回当前功能或类型中的一页代码元素，避免一次铺开所有方法。</summary>
    /// <returns>保留功能归属且最多含 30 个元素的局部结果。</returns>
    [AtlasComponent("atlas.explore.navigation.find", ArchitecturalRole.Presentation)]
    public ImmutableArray<CodeNode> VisibleNodes()
    {
        return MatchingNodes.Skip(State.Page * 30).Take(30).ToImmutableArray();
    }

    /// <summary>选中元素并定位它所属的功能和类型；方法选中后仍保留所属类型的成员列表。</summary>
    /// <param name="id">源码符号标识；null 用于清除选择。</param>
    [AtlasComponent("atlas.explore.navigation.path", ArchitecturalRole.Presentation)]
    public void Select(string? id)
    {
        if (id is not null && Workspace?.Report.Graph.Nodes.Any(node => node.Id == id) != true)
        {
            throw new ArgumentException("元素不在当前报告中。", nameof(id));
        }
        CodeNode? node = Workspace?.Report.Graph.Nodes.FirstOrDefault(item => item.Id == id);
        bool containsScopedMembers = node is not null && node.Kind is "type" or "interface" &&
            Workspace!.Report.Graph.Nodes.Any(member => member.ParentId == node.Id && BelongsTo(member.Feature, State.FeatureId));
        string? feature = node is not null && (State.FeatureId is null || (!BelongsTo(node.Feature, State.FeatureId) && !containsScopedMembers))
            ? FeatureFor(node.Feature) : State.FeatureId;
        string? container = node?.Kind is "type" or "interface" ? id : node?.ParentId;
        int mapPage = container == State.ClassId && feature == State.FeatureId ? State.MapPage : 0;
        if (node is not null && node.Id != container)
        {
            var members = Workspace!.Report.Graph.Nodes.Where(member => member.ParentId == container && BelongsTo(member.Feature, feature)).ToArray();
            int index = Array.FindIndex(members, member => member.Id == id);
            if (index >= 0) mapPage = index / MapPageSize;
        }
        Navigate(State with { SelectedId = id, FindingIndex = null, RelationIndex = null, FullSource = false, Panel = "graph",
            FeatureId = feature, ClassId = container, MapPage = mapPage, FocusPath = null, FocusLine = 0,
            Query = node?.Kind is "type" or "interface" || feature != State.FeatureId ? "" : State.Query,
            Scope = node is not null && State.Scope != node.Role ? null : State.Scope, Page = 0,
            Viewport = (State.Viewport ?? new()) with { DetailScroll = 0 } });
    }

    /// <summary>从规范或分工证据回到代码地图。有符号时选中符号；否则只打开报告快照中的文件行。</summary>
    /// <param name="nodeId">报告中的符号标识；不存在时改看文件。</param>
    /// <param name="path">报告原文路径。</param>
    /// <param name="line">从 1 开始的行号。</param>
    [AtlasComponent("atlas.explore.navigation.path", ArchitecturalRole.Presentation)]
    public void FocusEvidence(string? nodeId, string? path, int line)
    {
        if (Workspace is null)
        {
            return;
        }
        if (nodeId is not null && Workspace.Report.Graph.Nodes.Any(node => node.Id == nodeId))
        {
            Select(nodeId);
            return;
        }
        if (string.IsNullOrEmpty(path) || !Workspace.Report.Graph.Documents.Any(document => document.Path == path))
        {
            return;
        }
        Navigate(State with { SelectedId = null, FindingIndex = null, RelationIndex = null, Panel = "graph",
            FocusPath = path, FocusLine = Math.Max(1, line), FullSource = false, FeatureId = null, ClassId = null, Query = "",
            Scope = null, Page = 0, Viewport = (State.Viewport ?? new()) with { DetailScroll = 0 } });
    }

    /// <summary>打开一项诊断的说明与源码证据，并记录返回位置。</summary>
    /// <param name="index">当前报告中诊断的索引。</param>
    [AtlasComponent("atlas.explore.navigation.path", ArchitecturalRole.Presentation)]
    public void SelectFinding(int index)
    {
        if (Workspace is null || index < 0 || index >= Workspace.Report.Findings.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }
        Navigate(State with { FindingIndex = index, RelationIndex = null, FullSource = false, SelectedId = Workspace.Report.Findings[index].NodeId,
            Viewport = (State.Viewport ?? new()) with { DetailScroll = 0 } });
    }

    /// <summary>打开一条静态关系的来源证据，保留原有地图和功能路径。</summary>
    /// <param name="index">当前报告中关系的索引。</param>
    [AtlasComponent("atlas.explore.navigation.path", ArchitecturalRole.Presentation)]
    public void SelectRelation(int index)
    {
        if (Workspace is null || index < 0 || index >= Workspace.Report.Graph.Relations.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }
        Navigate(State with { RelationIndex = index, FindingIndex = null, FullSource = false,
            SelectedId = Workspace.Report.Graph.Relations[index].From,
            Viewport = (State.Viewport ?? new()) with { DetailScroll = 0 } });
    }

    /// <summary>切换源码片段与完整文件显示，并把显示方式记入当前浏览位置。</summary>
    public void ToggleFullSource()
    {
        State = State with { FullSource = !State.FullSource };
        Checkpoint();
    }

    /// <summary>在当前功能内部按技术职责筛选；不跳转到全仓库同职责列表。</summary>
    /// <param name="role">要查看的职责；null 表示本功能的所有职责。</param>
    [AtlasComponent("atlas.explore.navigation.find", ArchitecturalRole.Presentation)]
    public void SetScope(ArchitecturalRole? role) => Navigate(State with { Scope = role, SelectedId = null, ClassId = null, Page = 0, FindingIndex = null, RelationIndex = null,
        Viewport = (State.Viewport ?? new()) with { ListScroll = 0, DetailScroll = 0 } });
    /// <summary>在功能理解、结构检查与分析盲区之间切换，保留当前功能路径。</summary>
    /// <param name="panel">目标工作区面板标识。</param>
    public void SetPanel(string panel) => Navigate(State with { Panel = panel, SelectedId = panel == "graph" ? State.SelectedId : null, FindingIndex = null, RelationIndex = null });
    /// <summary>调整当前功能内优先展示的职责，不改变真实归属和依赖事实。</summary>
    /// <param name="perspective">实现、交互或数据视角。</param>
    public void SetPerspective(ExplorationPerspective perspective) => Navigate(State with { Perspective = perspective });

    /// <summary>更新用途或名称查询并从第一页展示；将查询保留为当前返回位置的一部分。</summary>
    /// <param name="query">用户输入的用途、符号名称、功能名或文件路径片段。</param>
    [AtlasComponent("atlas.explore.navigation.find", ArchitecturalRole.Presentation)]
    public void SetQuery(string query)
    {
        State = State with { Query = query, Page = 0, Viewport = (State.Viewport ?? new()) with { ListScroll = 0 } };
        Checkpoint();
    }

    /// <summary>记录地图缩放和平移以及两栏滚动位置，限制缩放范围。</summary>
    /// <param name="viewport">当前浏览窗口的位置快照。</param>
    [AtlasComponent("atlas.explore.navigation.history", ArchitecturalRole.Presentation)]
    public void SetViewport(ViewportState viewport)
    {
        State = State with { Viewport = viewport with { Zoom = Math.Clamp(viewport.Zoom, 0.55, 1.8) } };
        Checkpoint();
    }

    /// <summary>把当前源码元素加入或移出固定位置列表，供跨功能快速回访。</summary>
    public void TogglePin()
    {
        if (State.SelectedId is { } id && !pins.Add(id))
        {
            pins.Remove(id);
        }
    }

    /// <summary>回到上一个导航快照，恢复功能路径、搜索条件、选中项与窗口位置。</summary>
    [AtlasComponent("atlas.explore.navigation.history", ArchitecturalRole.Presentation)]
    public void Back()
    {
        if (CanBack)
        {
            mapPositions[(State.FeatureId, State.ClassId)] = State.Viewport ?? new();
            Checkpoint();
            State = history[--historyIndex];
        }
    }

    /// <summary>重新进入历史中的下一个位置，恢复离开时保存的探索状态。</summary>
    [AtlasComponent("atlas.explore.navigation.history", ArchitecturalRole.Presentation)]
    public void Forward()
    {
        if (CanForward)
        {
            mapPositions[(State.FeatureId, State.ClassId)] = State.Viewport ?? new();
            Checkpoint();
            State = history[++historyIndex];
        }
    }

    /// <summary>保存当前位置并加入新导航快照，分叉导航时移除旧的前进分支。</summary>
    /// <param name="state">导航后的完整探索状态。</param>
    [AtlasComponent("atlas.explore.navigation.history", ArchitecturalRole.Presentation)]
    private void Navigate(ExplorerState state)
    {
        var current = (State.FeatureId, State.ClassId);
        var next = (state.FeatureId, state.ClassId);
        mapPositions[current] = State.Viewport ?? new();
        if (current != next)
            state = state with { Viewport = mapPositions.GetValueOrDefault(next, new()) with { ListScroll = 0, DetailScroll = 0 } };
        if (state == State) return;
        Checkpoint();
        history.RemoveRange(historyIndex + 1, history.Count - historyIndex - 1);
        State = state;
        history.Add(state);
        if (history.Count > 200) history.RemoveAt(0);
        historyIndex = history.Count - 1;
    }

    /// <summary>把查询与滚动等非跳转修改同步到当前历史快照。</summary>
    [AtlasComponent("atlas.explore.navigation.history", ArchitecturalRole.Presentation)]
    private void Checkpoint()
    {
        if (historyIndex >= 0)
        {
            history[historyIndex] = State;
        }
    }
}
