using System.Collections.Immutable;
using CodeAtlas.Contracts;
using CodeAtlas.Framework;

namespace CodeAtlas.Presentation;

/// <summary>画布中的一个稳定位置；选择详情不改变其父级、坐标或展开状态。</summary>
[AtlasComponent("atlas.explore.map", ArchitecturalRole.Presentation)]
public sealed record MapBranch(string Key, FocusNode Node, string? ParentKey, string Feature, int Depth, double X, double Y);

/// <summary>保存原地展开的分支与布局，独立于右侧目录和详情的浏览路径。</summary>
[AtlasComponent("atlas.explore.map", ArchitecturalRole.Presentation)]
public sealed class ExpandableMap(GraphExplorerViewModel navigation)
{
    private AnalysisReport? report;
    private readonly Dictionary<string, MapBranch> placed = [];
    private readonly Dictionary<string, ImmutableArray<FocusNode>> children = [];
    private readonly HashSet<string> expanded = [];
    private readonly Dictionary<string, int> limits = [];
    private ImmutableArray<MapBranch> visible = [];
    private ImmutableArray<FocusEdge> edges = [];
    private bool dirty = true;

    /// <summary>只在报告更换时重置画布；选择与搜索不影响现有展开分支。</summary>
    public void Bind()
    {
        if (ReferenceEquals(report, navigation.Workspace?.Report)) return;
        report = navigation.Workspace?.Report;
        placed.Clear(); children.Clear(); expanded.Clear(); limits.Clear(); dirty = true;
        foreach (FeatureDefinition root in navigation.RootFeatures)
        {
            var node = FeatureNode(root);
            placed[node.Id] = new(node.Id, node, null, root.Id, 0, 20, 24 + placed.Count * 112);
        }
    }

    /// <summary>读取保留坐标的可见分支。</summary>
    public ImmutableArray<MapBranch> Branches { get { Rebuild(); return visible; } }
    /// <summary>读取按当前展开程度聚合的真实源码关系。</summary>
    public ImmutableArray<FocusEdge> Edges { get { Rebuild(); return edges; } }
    /// <summary>检查一个节点是否已展开。</summary>
    /// <param name="key">画布分支键。</param>
    /// <returns>该节点的展开状态。</returns>
    public bool IsExpanded(string key) => expanded.Contains(key);
    /// <summary>计算当前分支的直接下级，不把方法当成目录。</summary>
    /// <param name="branch">要读取的画布分支。</param>
    /// <returns>可展开的直接下级数。</returns>
    public int ChildCount(MapBranch branch) => Children(branch).Length;
    /// <summary>读取尚未添加到画布的下级数量。</summary>
    /// <param name="branch">已展开分支。</param>
    /// <returns>可通过继续展开添加的数量。</returns>
    public int Remaining(MapBranch branch) => Math.Max(0, ChildCount(branch) - limits.GetValueOrDefault(branch.Key, 6));

    /// <summary>原地展开或收起；保留后代状态与坐标，重新展开时恢复。</summary>
    /// <param name="branch">用户点选展开按钮的分支。</param>
    public void Toggle(MapBranch branch)
    {
        if (ChildCount(branch) == 0) return;
        if (!expanded.Remove(branch.Key)) { expanded.Add(branch.Key); PlaceChildren(branch); }
        dirty = true;
    }

    /// <summary>在已显示下级之后追加六项，不用分页替换原节点。</summary>
    /// <param name="branch">要继续加载的分支。</param>
    public void More(MapBranch branch)
    {
        limits[branch.Key] = limits.GetValueOrDefault(branch.Key, 6) + 6;
        PlaceChildren(branch); dirty = true;
    }

    /// <summary>收起所有分支，保留已分配坐标；只有用户显式操作才改变展开状态。</summary>
    public void CollapseAll() { expanded.Clear(); dirty = true; }

    private static FocusNode FeatureNode(FeatureDefinition feature) => new("f:" + feature.Id, feature.Name, feature.Purpose, feature.Id, null, "功能");
    private static FocusNode SymbolNode(CodeNode node) => new("s:" + node.Id, node.Name,
        string.IsNullOrWhiteSpace(node.Summary) ? "用途未说明 · 查看源码" : node.Summary, null, node.Id,
        node.Kind is "type" or "interface" ? "类型 / 契约" : "成员 · 查看详情");

    private ImmutableArray<FocusNode> Children(MapBranch branch)
    {
        if (children.TryGetValue(branch.Key, out var cached)) return cached;
        if (report is null) return [];
        var result = new List<FocusNode>();
        if (branch.Node.FeatureId is { } feature)
        {
            result.AddRange(navigation.Features.Where(item => item.ParentId == feature && item.Id != feature &&
                !Ancestors(branch).Any(parent => parent.Node.FeatureId == item.Id)).Select(FeatureNode));
            var direct = report.Graph.Nodes.Where(node => navigation.FeatureFor(node.Feature) == feature).ToArray();
            var parents = direct.Select(node => node.ParentId).ToHashSet();
            result.AddRange(report.Graph.Nodes.Where(node => node.Kind is "type" or "interface" &&
                (navigation.FeatureFor(node.Feature) == feature || parents.Contains(node.Id))).Select(SymbolNode));
        }
        else if (navigation.FindNode(branch.Node.SymbolId) is { Kind: "type" or "interface" } type)
            result.AddRange(report.Graph.Nodes.Where(node => node.ParentId == type.Id && navigation.BelongsTo(node.Feature, branch.Feature)).Select(SymbolNode));
        children[branch.Key] = result.ToImmutableArray();
        return children[branch.Key];
    }

    private IEnumerable<MapBranch> Ancestors(MapBranch branch)
    {
        for (MapBranch? current = branch; current is not null; current = current.ParentKey is { } parent ? placed.GetValueOrDefault(parent) : null)
            yield return current;
    }

    private void PlaceChildren(MapBranch branch)
    {
        foreach (FocusNode child in Children(branch).Take(limits.GetValueOrDefault(branch.Key, 6)))
        {
            string key = branch.Key + "/" + child.Id;
            if (placed.ContainsKey(key)) continue;
            int depth = branch.Depth + 1;
            double y = Math.Max(branch.Y, placed.Values.Where(item => item.Depth == depth).Select(item => item.Y + 112).DefaultIfEmpty(24).Max());
            placed[key] = new(key, child, branch.Key, child.FeatureId ?? branch.Feature, depth, 20 + depth * 300, y);
        }
    }

    private void Rebuild()
    {
        Bind();
        if (!dirty) return;
        visible = placed.Values.Where(branch => Ancestors(branch).Skip(1).All(parent => expanded.Contains(parent.Key))).ToImmutableArray();
        if (report is null) { edges = []; dirty = false; return; }
        var symbolOwners = visible.Where(branch => branch.Node.SymbolId is not null).GroupBy(branch => branch.Node.SymbolId!)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(branch => branch.Depth).ToArray());
        var featureOwners = visible.Where(branch => branch.Node.FeatureId is not null).GroupBy(branch => branch.Node.FeatureId!)
            .ToDictionary(group => group.Key, group => group.First().Key);
        var owners = new Dictionary<string, string>();
        foreach (CodeNode node in report.Graph.Nodes)
        {
            MapBranch? owner = symbolOwners.GetValueOrDefault(node.Id)?.FirstOrDefault(branch => navigation.BelongsTo(node.Feature, branch.Feature));
            if (owner is null && node.ParentId is { } parent) owner = symbolOwners.GetValueOrDefault(parent)?.FirstOrDefault(branch => navigation.BelongsTo(node.Feature, branch.Feature));
            if (owner is not null) { owners[node.Id] = owner.Key; continue; }
            foreach (FeatureDefinition feature in navigation.FeaturePath(navigation.FeatureFor(node.Feature)).Reverse())
                if (featureOwners.TryGetValue(feature.Id, out string? key)) { owners[node.Id] = key; break; }
        }
        edges = report.Graph.Relations.Select((relation, index) => (relation, index))
            .Where(item => owners.ContainsKey(item.relation.From) && owners.ContainsKey(item.relation.To))
            .Select(item => (From: owners[item.relation.From], To: owners[item.relation.To], item.index)).Where(item => item.From != item.To)
            .GroupBy(item => (item.From, item.To)).Select(group => new FocusEdge(group.Key.From, group.Key.To, group.Count(), group.First().index)).ToImmutableArray();
        dirty = false;
    }
}
