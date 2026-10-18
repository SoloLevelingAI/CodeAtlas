namespace CodeAtlas.Exploration;

/// <summary>图形与命令行控制共用的浏览状态；选择和展开只修改局部对象。</summary>
public sealed class ExplorerSession : ObservableObject
{
    private ReportIndex? index;
    private ExplorerItem? selected;
    private string query = "";
    private string view = "tree";
    private ExplorerItem? currentModule;
    public ActionHistory History { get; } = new();
    public ReportIndex? Index => index;
    public ExplorerItem? Selected => selected;
    public ExplorerItem? CurrentModule => currentModule;
    public string Query => query;
    public string View => view;
    public bool CanUndo => History.CanUndo;
    public bool CanRedo => History.CanRedo;
    public IReadOnlyList<ExplorerItem> Roots => index?.Roots ?? [];
    public IReadOnlyList<ExplorerItem> SearchResults { get; private set; } = [];
    public string Source => index is not null && selected is not null ? index.SourceText(selected) : "选择一个功能、类或方法查看源码。";
    public bool CanViewSource => selected?.Source is { Line: > 0 };
    public string Breadcrumb => selected is null ? "功能总览" : string.Join(" / ", Ancestors(selected).Reverse().Select(n => n.Title));
    public string DetailTitle => selected?.Title ?? "选择要理解的功能";
    public string DetailSummary => selected?.Summary ?? "按模块、子功能、职责和类型查找实现；选择只更新详情，目录与地图位置始终保留。";
    public string DetailInputs => string.IsNullOrWhiteSpace(selected?.Inputs) ? "当前节点未声明输入说明。" : selected.Inputs;
    public string DetailOutputs => string.IsNullOrWhiteSpace(selected?.Outputs) ? "当前节点未声明输出说明。" : selected.Outputs;
    public event Action<ExplorerItem>? LocateRequested;

    /// <summary>原子替换索引并释放旧导航历史；不会给每次点击创建报告副本。</summary>
    public void Load(ReportIndex value)
    {
        index = value; selected = null; currentModule = null; query = ""; view = "tree";
        SearchResults = []; History.Clear();
        foreach (ExplorerItem root in Roots.Take(1)) root.IsExpanded = true;
        Notify(); Changed(nameof(Index)); Changed(nameof(Roots)); Changed(nameof(SearchResults));
    }

    /// <summary>选择元素并按需展开祖先；同一动作可撤销，定位请求只作用于目录滚动。</summary>
    public void Select(string id, bool locate = true)
    {
        ExplorerItem target = RequireIndex().Resolve(id);
        if (ReferenceEquals(target, selected)) { if (locate) Locate(target); return; }
        ExplorerItem? before = selected;
        ExplorerItem[] collapsed = Ancestors(target).Skip(1).Where(n => !n.IsExpanded).ToArray();
        History.Execute(() => { foreach (ExplorerItem item in collapsed) item.IsExpanded = false; ApplySelection(before, locate); },
            () => { foreach (ExplorerItem item in collapsed.Reverse()) item.IsExpanded = true; ApplySelection(target, locate); });
        Notify();
    }

    /// <summary>设置单个节点的展开状态，撤销时恢复原状态。</summary>
    public void Expand(string id, bool expanded)
    {
        ExplorerItem item = RequireIndex().Resolve(id);
        if (!item.HasChildren || item.IsExpanded == expanded) return;
        bool before = item.IsExpanded;
        History.Execute(() => item.IsExpanded = before, () => item.IsExpanded = expanded);
        Notify();
    }

    /// <summary>执行有限结果的用途搜索；不修改主目录的展开状态。</summary>
    public void Search(string value)
    {
        query = value;
        SearchResults = index?.Search(value) ?? [];
        Changed(nameof(Query)); Changed(nameof(SearchResults));
    }

    /// <summary>切换图或树视图，保留选择与展开项。</summary>
    public void SetView(string value)
    {
        if (value is not ("tree" or "graph")) throw new NotSupportedException("视图只支持 tree 或 graph。");
        if (view == value) return;
        string prior = view;
        History.Execute(() => { view = prior; Changed(nameof(View)); }, () => { view = value; Changed(nameof(View)); });
        Notify();
    }

    /// <summary>根据真实目录滚动位置更新小地图标记，不写入撤销历史。</summary>
    public void TrackVisibleModule(ExplorerItem? item)
    {
        if (ReferenceEquals(currentModule, item)) return;
        if (currentModule is not null) currentModule.IsCurrent = false;
        currentModule = item;
        if (item is not null) item.IsCurrent = true;
        Changed(nameof(CurrentModule));
    }

    /// <summary>撤销上次选择、展开或视图切换。</summary>
    public void Undo() { History.Undo(); Notify(); }
    /// <summary>重做上次撤销的动作。</summary>
    public void Redo() { History.Redo(); Notify(); }

    /// <summary>应用选择并只通知详情相关属性。</summary>
    private void ApplySelection(ExplorerItem? item, bool locate)
    {
        if (selected is not null) selected.IsSelected = false;
        selected = item;
        if (item is not null)
        {
            item.IsSelected = true;
            if (locate) Locate(item);
        }
        Notify();
    }

    /// <summary>请求目录定位，同时记住所属模块。</summary>
    private void Locate(ExplorerItem item)
    {
        foreach (ExplorerItem parent in Ancestors(item).Skip(1).Reverse()) parent.IsExpanded = true;
        TrackVisibleModule(Ancestors(item).Last());
        LocateRequested?.Invoke(item);
    }

    /// <summary>沿对象父级枚举路径；集合大小与路径深度相关。</summary>
    private static IEnumerable<ExplorerItem> Ancestors(ExplorerItem item)
    {
        for (ExplorerItem? current = item; current is not null; current = current.Parent) yield return current;
    }

    /// <summary>要求已加载报告才能处理定位指令。</summary>
    private ReportIndex RequireIndex() => index ?? throw new InvalidOperationException("尚未加载分析报告。");

    /// <summary>通知详情和历史状态，保留节点集合的引用。</summary>
    private void Notify()
    {
        foreach (string name in new[] { nameof(Selected), nameof(Source), nameof(CanViewSource), nameof(Breadcrumb), nameof(CanUndo), nameof(CanRedo), nameof(Query), nameof(View), nameof(DetailTitle), nameof(DetailSummary), nameof(DetailInputs), nameof(DetailOutputs) }) Changed(name);
    }
}
