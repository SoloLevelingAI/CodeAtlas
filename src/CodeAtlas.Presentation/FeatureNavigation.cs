using System.Collections.Immutable;
using CodeAtlas.Contracts;
using CodeAtlas.Framework;

namespace CodeAtlas.Presentation;

public sealed partial class GraphExplorerViewModel
{
    private const string Unassigned = "__unassigned__";
    private AnalysisReport? featureReport;
    private ImmutableArray<FeatureDefinition> featureCache = [];
    private Dictionary<string, FeatureDefinition> featureById = [];
    private Dictionary<string, ImmutableArray<FeatureDefinition>> pathsByFeature = [];
    private Dictionary<string, string> rootsByFeature = [];
    private Dictionary<string, string> labelsByFeature = [];
    private Dictionary<string, int> countsByFeature = [];
    private Dictionary<string, CodeNode> nodesById = [];
    private ImmutableArray<FeatureDefinition> rootCache = [];
    private AnalysisReport? queryReport;
    private (string? Feature, string? Class, ArchitecturalRole? Role, string Query, ExplorationPerspective Perspective)? queryKey;
    private ImmutableArray<CodeNode> queryCache = [];
    public ImmutableArray<FeatureDefinition> Features
    {
        get
        {
            if (ReferenceEquals(featureReport, Workspace?.Report)) return featureCache;
            var declared = Workspace?.Report.Graph.Features ?? [];
            if (declared.IsDefault) declared = [];
            var catalog = declared.DistinctBy(feature => feature.Id).ToImmutableArray();
            if (Workspace?.Report.Graph.Nodes.Any(node => !catalog.Any(feature => feature.Id == node.Feature)) == true)
                catalog = catalog.Add(new(Unassigned, "待归属代码", "缺少功能层级声明；这里只保留索引，不推断业务模块。请先查看结构问题。", null, FeatureKind.Module, "", "", new("", 1, 1)));
            featureReport = Workspace?.Report;
            featureCache = catalog;
            BuildFeatureIndex();
            return catalog;
        }
    }

    public FeatureDefinition? CurrentFeature { get { _ = Features; return State.FeatureId is { } id ? featureById.GetValueOrDefault(id) : null; } }
    public CodeNode? CurrentClass => FindNode(State.ClassId);
    public ImmutableArray<FeatureDefinition> RootFeatures { get { _ = Features; return rootCache; } }
    public int MatchingNodeCount => MatchingNodes.Length;

    /// <summary>复用同一报告和筛选条件的查询结果，地图位置变化不触发重新筛选排序。</summary>
    private ImmutableArray<CodeNode> MatchingNodes
    {
        get
        {
            var key = (State.FeatureId, State.ClassId, State.Scope, State.Query, State.Perspective);
            if (!ReferenceEquals(queryReport, Workspace?.Report) || queryKey != key)
            {
                queryCache = ScopedNodes().ToImmutableArray();
                queryReport = Workspace?.Report;
                queryKey = key;
            }
            return queryCache;
        }
    }

    /// <summary>通过当前报告的符号索引定位元素，避免在每条关系上重复扫描全部符号。</summary>
    /// <param name="id">元素标识；null 表示无选择。</param>
    /// <returns>对应元素；不存在时返回 null。</returns>
    public CodeNode? FindNode(string? id) { _ = Features; return id is null ? null : nodesById.GetValueOrDefault(id); }

    /// <summary>每份报告只构建一次功能路径、根节点、元素计数和符号查找表。</summary>
    private void BuildFeatureIndex()
    {
        featureById = featureCache.ToDictionary(feature => feature.Id, StringComparer.Ordinal);
        pathsByFeature = []; rootsByFeature = []; labelsByFeature = []; countsByFeature = [];
        nodesById = Workspace!.Report.Graph.Nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        foreach (FeatureDefinition feature in featureCache)
        {
            var path = new List<FeatureDefinition>();
            var visited = new HashSet<string>(StringComparer.Ordinal);
            string? id = feature.Id;
            while (id is not null && visited.Add(id) && featureById.TryGetValue(id, out FeatureDefinition? entry))
            { path.Insert(0, entry); id = entry.ParentId; }
            pathsByFeature[feature.Id] = path.ToImmutableArray();
            rootsByFeature[feature.Id] = path[0].ParentId is { } parent && featureById.ContainsKey(parent) ? feature.Id : path[0].Id;
            labelsByFeature[feature.Id] = string.Join(" / ", path.Select(item => item.Name));
        }
        rootCache = featureCache.Where(feature => rootsByFeature[feature.Id] == feature.Id).ToImmutableArray();
        foreach (CodeNode node in nodesById.Values)
        {
            string id = featureById.ContainsKey(node.Feature) ? node.Feature : Unassigned;
            foreach (FeatureDefinition ancestor in pathsByFeature.GetValueOrDefault(id, []))
                countsByFeature[ancestor.Id] = countsByFeature.GetValueOrDefault(ancestor.Id) + 1;
        }
    }

    /// <summary>读取当前路径下的下一层功能，而非展开整个仓库的类型与方法。</summary>
    /// <returns>当前父级的直接子功能；搜索或进入类型后不显示功能卡片。</returns>
    [AtlasComponent("atlas.explore.navigation.path", ArchitecturalRole.Presentation)]
    public ImmutableArray<FeatureDefinition> ChildFeatures() => State.ClassId is not null || !string.IsNullOrWhiteSpace(State.Query) ? [] :
        (State.FeatureId is null ? RootFeatures : Features.Where(feature => feature.ParentId == State.FeatureId && feature.Id != State.FeatureId).ToImmutableArray());

    /// <summary>进入功能层级，清除旧层的筛选；原搜索与位置由浏览历史保留。</summary>
    /// <param name="id">目标功能标识；null 表示模块总览。</param>
    [AtlasComponent("atlas.explore.navigation.path", ArchitecturalRole.Presentation)]
    public void SetFeature(string? id)
    {
        if (id is not null && !Features.Any(feature => feature.Id == id)) throw new ArgumentException("功能不在当前目录中。", nameof(id));
        Navigate(State with { FeatureId = id, ClassId = null, SelectedId = null, FindingIndex = null, RelationIndex = null,
            Scope = null, Query = "", Panel = "graph", Page = 0, MapPage = 0, FocusPath = null, FocusLine = 0,
            Viewport = (State.Viewport ?? new()) with { ListScroll = 0, DetailScroll = 0 } });
    }

    /// <summary>沿显式结构返回：成员回到类型，类型回到功能，功能回到父功能。</summary>
    [AtlasComponent("atlas.explore.navigation.path", ArchitecturalRole.Presentation)]
    public void UpLevel()
    {
        if (CurrentClass is { } type && Selected?.Id != type.Id) { Select(type.Id); return; }
        if (State.ClassId is not null) { SetFeature(State.FeatureId); return; }
        UpFeature();
    }

    /// <summary>离开当前类型或进入父功能；无有效父级时回到全局总览。</summary>
    [AtlasComponent("atlas.explore.navigation.path", ArchitecturalRole.Presentation)]
    public void UpFeature()
    {
        if (State.ClassId is not null) { SetFeature(State.FeatureId); return; }
        string? parent = CurrentFeature?.ParentId;
        SetFeature(parent != State.FeatureId && Features.Any(feature => feature.Id == parent) ? parent : null);
    }

    /// <summary>列出当前功能的同级目录，允许横向切换而不丢失父级入口。</summary>
    /// <returns>显式父级相同的功能；总览中返回根模块。</returns>
    public ImmutableArray<FeatureDefinition> SiblingFeatures() => CurrentFeature is { } feature
        ? Features.Where(item => item.ParentId == feature.ParentId).ToImmutableArray() : RootFeatures;

    /// <summary>逐级列出功能路径，用于可点击的面包屑和搜索结果归属。</summary>
    /// <param name="id">要追溯的功能标识。</param>
    /// <returns>由模块到当前功能的路径；错误父级或循环处停止。</returns>
    [AtlasComponent("atlas.explore.navigation.path", ArchitecturalRole.Presentation)]
    public ImmutableArray<FeatureDefinition> FeaturePath(string? id)
    {
        _ = Features;
        return id is null ? [] : pathsByFeature.GetValueOrDefault(id, []);
    }

    public string FeatureFor(string id) { _ = Features; return featureById.ContainsKey(id) ? id : Unassigned; }
    public string RootOf(string id)
    {
        _ = Features;
        return rootsByFeature.GetValueOrDefault(FeatureFor(id), Unassigned);
    }

    public bool BelongsTo(string feature, string? ancestor) => ancestor is null ||
        FeatureFor(feature) == ancestor || FeaturePath(FeatureFor(feature)).Any(item => item.Id == ancestor);
    public int FeatureCount(string id) { _ = Features; return countsByFeature.GetValueOrDefault(id); }
    public string FeatureLabel(string id) { string feature = FeatureFor(id); return labelsByFeature.GetValueOrDefault(feature, ""); }

    /// <summary>切换局部列表分页；分页只控制显示数量，不掩盖结构缺失诊断。</summary>
    /// <param name="page">从零开始的页号。</param>
    [AtlasComponent("atlas.explore.navigation.find", ArchitecturalRole.Presentation)]
    public void SetPage(int page) => Navigate(State with { Page = Math.Clamp(page, 0, Math.Max(0, (MatchingNodeCount - 1) / 30)),
        Viewport = (State.Viewport ?? new()) with { ListScroll = 0 } });

    /// <summary>按显式功能、所属类型和用途查询取得局部结果，跨功能搜索时仍保留功能路径。</summary>
    /// <returns>按功能与当前职责优先级排列的元素序列。</returns>
    [AtlasComponent("atlas.explore.navigation.find", ArchitecturalRole.Presentation)]
    private IEnumerable<CodeNode> ScopedNodes()
    {
        if (Workspace is null) return [];
        var all = Workspace.Report.Graph.Nodes;
        IEnumerable<CodeNode> nodes = all.Where(node => BelongsTo(node.Feature, State.FeatureId));
        if (State.ClassId is { } container) nodes = nodes.Where(node => node.ParentId == container);
        if (State.Scope is { } role) nodes = nodes.Where(node => node.Role == role);
        if (!string.IsNullOrWhiteSpace(State.Query))
        {
            nodes = nodes.Where(node => (node.Name + " " + node.Symbol + " " + node.Summary + " " + FeatureLabel(node.Feature) + " " + node.Source.Path)
                .Contains(State.Query, StringComparison.OrdinalIgnoreCase));
        }
        else if (State.ClassId is { } type)
        {
            nodes = nodes.Where(node => node.ParentId == type);
        }
        else if (State.FeatureId is { } feature)
        {
            var direct = nodes.Where(node => FeatureFor(node.Feature) == feature).ToArray();
            var parentIds = direct.Select(node => node.ParentId).Where(id => id is not null).ToHashSet();
            nodes = all.Where(node => node.Kind is "type" or "interface" &&
                (direct.Contains(node) || parentIds.Contains(node.Id)) && (State.Scope is null || node.Role == State.Scope));
        }
        else return [];
        return nodes.OrderBy(node => node.Feature, StringComparer.Ordinal).ThenBy(node => node.Role == FocusRole ? 0 : 1)
            .ThenBy(node => node.Role).ThenBy(node => node.Name, StringComparer.Ordinal).ThenBy(node => node.Id, StringComparer.Ordinal);
    }
}
