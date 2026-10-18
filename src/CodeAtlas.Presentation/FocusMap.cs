using System.Collections.Immutable;
using CodeAtlas.Contracts;
using CodeAtlas.Framework;

namespace CodeAtlas.Presentation;

/// <summary>局部地图的可导航节点；功能入口与代码入口使用不同目标，避免误跳。</summary>
[AtlasComponent("atlas.explore.map", ArchitecturalRole.Presentation)]
public sealed record FocusNode(string Id, string Name, string Purpose, string? FeatureId, string? SymbolId,
    string Kind, bool External = false);

/// <summary>局部地图中聚合的真实静态关系，保留第一条源码证据的索引。</summary>
[AtlasComponent("atlas.explore.map", ArchitecturalRole.Presentation)]
public sealed record FocusEdge(string From, string To, int Count, int EvidenceIndex);

/// <summary>一层地图的节点、关系和分页信息；父子组织线由锚点单独绘制。</summary>
[AtlasComponent("atlas.explore.map", ArchitecturalRole.Presentation)]
public sealed record FocusMap(FocusNode? Anchor, ImmutableArray<FocusNode> Nodes,
    ImmutableArray<FocusNode> Neighbors, ImmutableArray<FocusEdge> Edges, int Total, int HiddenNeighbors, int Page);

public sealed partial class GraphExplorerViewModel
{
    private const int MapPageSize = 6;
    private AnalysisReport? focusReport;
    private (string? Feature, string? Class, int Page)? focusKey;
    private FocusMap focusCache = new(null, [], [], [], 0, 0, 0);

    /// <summary>构建当前层的关系地图；只有报告、功能、类型或地图分页变化才重新聚合。</summary>
    /// <returns>含真实关系证据及外部邻居的局部地图，搜索和悬停不会重建地图。</returns>
    [AtlasComponent("atlas.explore.map", ArchitecturalRole.Presentation)]
    public FocusMap CurrentMap()
    {
        var key = (State.FeatureId, State.ClassId, State.MapPage);
        if (ReferenceEquals(focusReport, Workspace?.Report) && focusKey == key) return focusCache;
        focusCache = BuildFocusMap();
        focusReport = Workspace?.Report;
        focusKey = key;
        return focusCache;
    }

    /// <summary>切换本层地图的六项分组；完整代码目录仍保留在右侧。</summary>
    /// <param name="page">从零开始的地图页号。</param>
    [AtlasComponent("atlas.explore.map", ArchitecturalRole.Presentation)]
    public void SetMapPage(int page) => Navigate(State with { MapPage = Math.Clamp(page, 0, Math.Max(0, (CurrentMap().Total - 1) / MapPageSize)), Viewport = new() });

    /// <summary>将源码符号映射到当前可见层，区分父子归属与源码依赖，不虚构调用。</summary>
    /// <returns>当前层的关系快照；越界关系聚合为可进入的外部功能。</returns>
    [AtlasComponent("atlas.explore.map", ArchitecturalRole.Presentation)]
    private FocusMap BuildFocusMap()
    {
        if (Workspace is null) return new(null, [], [], [], 0, 0, 0);
        _ = Features;
        var graph = Workspace.Report.Graph;
        FocusNode AsFeature(FeatureDefinition feature, bool external = false) => new("f:" + feature.Id, feature.Name,
            feature.Purpose, feature.Id, null, "功能", external);
        static FocusNode AsSymbol(CodeNode node) => new("s:" + node.Id, node.Name,
            string.IsNullOrWhiteSpace(node.Summary) ? "用途未说明 · 可查看源码" : node.Summary, null, node.Id,
            node.Kind is "type" or "interface" ? "类型 / 契约" : "成员 · 查看详情");

        FeatureDefinition? current = CurrentFeature;
        CodeNode? type = CurrentClass;
        FocusNode? anchor = type is not null ? AsSymbol(type) : current is not null ? AsFeature(current) : null;
        var candidates = new List<FocusNode>();
        Dictionary<string, string> buckets = [];
        if (type is not null)
        {
            foreach (CodeNode node in graph.Nodes.Where(node => node.ParentId == type.Id && BelongsTo(node.Feature, State.FeatureId)))
            { candidates.Add(AsSymbol(node)); buckets[node.Id] = "s:" + node.Id; }
            buckets[type.Id] = anchor!.Id;
        }
        else if (current is not null)
        {
            var children = Features.Where(feature => feature.ParentId == current.Id && feature.Id != current.Id).ToArray();
            candidates.AddRange(children.Select(feature => AsFeature(feature)));
            var direct = graph.Nodes.Where(node => FeatureFor(node.Feature) == current.Id).ToArray();
            var parentIds = direct.Select(node => node.ParentId).Where(id => id is not null).ToHashSet();
            var types = graph.Nodes.Where(node => node.Kind is "type" or "interface" &&
                (FeatureFor(node.Feature) == current.Id || parentIds.Contains(node.Id))).ToArray();
            candidates.AddRange(types.Select(AsSymbol));
            var typeIds = types.Select(node => node.Id).ToHashSet();
            foreach (CodeNode node in graph.Nodes)
            {
                FeatureDefinition? child = children.FirstOrDefault(feature => BelongsTo(node.Feature, feature.Id));
                if (child is not null) buckets[node.Id] = "f:" + child.Id;
                else if (FeatureFor(node.Feature) == current.Id || typeIds.Contains(node.Id))
                {
                    string? owner = typeIds.Contains(node.Id) ? node.Id : node.ParentId;
                    buckets[node.Id] = owner is not null && typeIds.Contains(owner) ? "s:" + owner : anchor!.Id;
                }
            }
        }
        else
        {
            candidates.AddRange(RootFeatures.Select(feature => AsFeature(feature)));
            foreach (CodeNode node in graph.Nodes) buckets[node.Id] = "f:" + RootOf(node.Feature);
        }

        int page = Math.Clamp(State.MapPage, 0, Math.Max(0, (candidates.Count - 1) / MapPageSize));
        var visible = candidates.Skip(page * MapPageSize).Take(MapPageSize).ToImmutableArray();
        var visibleIds = visible.Select(node => node.Id).ToHashSet();
        if (anchor is not null) visibleIds.Add(anchor.Id);
        var outside = new Dictionary<string, FocusNode>();
        var relations = new List<(string From, string To, int Index)>();
        string? Resolve(string id)
        {
            if (buckets.TryGetValue(id, out string? bucket)) return visibleIds.Contains(bucket) ? bucket : null;
            if (!nodesById.TryGetValue(id, out CodeNode? node)) return null;
            string featureId = FeatureFor(node.Feature);
            if (!featureById.TryGetValue(featureId, out FeatureDefinition? feature)) return null;
            string externalId = "external:" + featureId;
            outside[externalId] = AsFeature(feature, true) with { Id = externalId };
            return externalId;
        }
        for (int index = 0; index < graph.Relations.Length; index++)
        {
            CodeRelation relation = graph.Relations[index];
            bool fromVisible = buckets.TryGetValue(relation.From, out string? fromBucket) && visibleIds.Contains(fromBucket);
            bool toVisible = buckets.TryGetValue(relation.To, out string? toBucket) && visibleIds.Contains(toBucket);
            if (!fromVisible && !toVisible) continue;
            string? from = Resolve(relation.From), to = Resolve(relation.To);
            if (from is not null && to is not null && from != to) relations.Add((from, to, index));
        }
        var neighborIds = relations.SelectMany(edge => new[] { edge.From, edge.To }).Where(outside.ContainsKey)
            .GroupBy(id => id).OrderByDescending(group => group.Count()).ThenBy(group => group.Key, StringComparer.Ordinal).Select(group => group.Key).ToArray();
        var neighbors = neighborIds.Take(4).Select(id => outside[id]).ToImmutableArray();
        visibleIds.UnionWith(neighbors.Select(node => node.Id));
        var edges = relations.Where(edge => visibleIds.Contains(edge.From) && visibleIds.Contains(edge.To))
            .GroupBy(edge => (edge.From, edge.To)).Select(group => new FocusEdge(group.Key.From, group.Key.To, group.Count(), group.First().Index)).ToImmutableArray();
        return new(anchor, visible, neighbors, edges, candidates.Count, Math.Max(0, neighborIds.Length - neighbors.Length), page);
    }
}
