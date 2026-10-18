using CodeAtlas.Contracts;
using CodeAtlas.Framework;

namespace CodeAtlas.Exploration;

/// <summary>报告的只读查询索引；按功能、职责、类型组织符号，并缓存已访问的展示对象。</summary>
[AtlasComponent("atlas.explore.desktop.search", ArchitecturalRole.Presentation)]
public sealed class ReportIndex
{
    private const string Loose = "__unassigned";
    private readonly Dictionary<string, FeatureDefinition> features;
    private readonly Dictionary<string, CodeNode> symbols;
    private readonly Dictionary<string, string?> featureParents = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> symbolParents = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> children = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ExplorerItem> items = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> groupLabels = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CodeNode> contexts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<CodeRelation>> relations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SourceDocument> documents;
    public AnalysisReport Report { get; }
    public IReadOnlyList<ExplorerItem> Roots { get; }
    public IReadOnlyList<ModuleLink> ModuleLinks { get; }
    public int MaterializedCount => items.Count;

    /// <summary>一次性建立邻接索引；未声明、孤立和循环功能均保留在待整理分组。</summary>
    /// <param name="report">不在浏览过程中被修改的源码快照。</param>
    public ReportIndex(AnalysisReport report)
    {
        Report = report;
        features = (report.Graph.Features.IsDefault ? [] : report.Graph.Features)
            .GroupBy(f => f.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        symbols = report.Graph.Nodes.GroupBy(n => n.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        documents = report.Graph.Documents.GroupBy(d => d.Path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        bool loose = false;
        foreach (string id in features.Keys.ToArray())
        {
            string? parent = features[id].ParentId;
            var visited = new HashSet<string>(StringComparer.Ordinal) { id };
            string? cursor = parent;
            bool invalid = false;
            while (cursor is not null)
            {
                if (!features.TryGetValue(cursor, out FeatureDefinition? ancestor) || !visited.Add(cursor)) { invalid = true; break; }
                cursor = ancestor.ParentId;
            }
            featureParents[id] = invalid ? Loose : parent;
            loose |= invalid;
        }
        foreach (CodeNode node in symbols.Values)
        {
            if (!features.ContainsKey(node.Feature))
            {
                features[node.Feature] = new(node.Feature, node.Feature.Length == 0 ? "未声明功能" : node.Feature,
                    "没有有效的功能目录声明；保留符号以供整理。", Loose, FeatureKind.Capability, "", "", node.Source);
                featureParents[node.Feature] = Loose;
                loose = true;
            }
        }
        if (loose)
        {
            features[Loose] = new(Loose, "待整理代码", "未声明功能、缺失父级或层级循环；这些代码仍然纳入索引。",
                null, FeatureKind.Module, "", "", new("", 0, 0));
            featureParents[Loose] = null;
        }
        foreach ((string id, string? parent) in featureParents)
            if (parent is not null) AddChild("feature:" + parent, "feature:" + id);

        foreach (CodeNode node in symbols.Values)
        {
            string key = "node:" + node.Id;
            string group = $"role:{node.Feature}:{node.Role?.ToString() ?? "Unknown"}";
            string parent = group;
            if (node.ParentId is not null && symbols.TryGetValue(node.ParentId, out CodeNode? type) &&
                type.Feature == node.Feature && type.Role == node.Role && type.Id != node.Id)
                parent = "node:" + type.Id;
            else if (node.ParentId is not null && symbols.TryGetValue(node.ParentId, out CodeNode? owner) && owner.Id != node.Id)
            {
                parent = $"context:{node.Feature}:{node.Role}:{owner.Id}";
                if (contexts.TryAdd(parent, owner)) { symbolParents[parent] = group; AddChild(group, parent); }
            }
            symbolParents[key] = parent;
            AddChild(parent, key);
            if (!groupLabels.ContainsKey(group))
            {
                groupLabels[group] = RoleLabel(node.Role);
                AddChild("feature:" + node.Feature, group);
                symbolParents[group] = "feature:" + node.Feature;
            }
        }
        foreach (string key in symbolParents.Keys)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal) { key };
            string? cursor = symbolParents[key];
            while (cursor is not null && symbolParents.TryGetValue(cursor, out string? parent))
            {
                if (!seen.Add(cursor)) throw new InvalidDataException("报告中的符号父级存在循环：" + key);
                cursor = parent;
            }
        }
        foreach (List<string> list in children.Values) list.Sort((a, b) =>
        {
            SourceLocation? sa = a.StartsWith("feature:", StringComparison.Ordinal) ? features[a[8..]].Source : a.StartsWith("node:", StringComparison.Ordinal) ? symbols[a[5..]].Source : null;
            SourceLocation? sb = b.StartsWith("feature:", StringComparison.Ordinal) ? features[b[8..]].Source : b.StartsWith("node:", StringComparison.Ordinal) ? symbols[b[5..]].Source : null;
            int order = string.Compare(sa?.Path ?? a, sb?.Path ?? b, StringComparison.Ordinal);
            return order != 0 ? order : (sa?.Line ?? 0).CompareTo(sb?.Line ?? 0);
        });
        List<ExplorerItem> roots = featureParents.Where(p => p.Value is null).OrderBy(p => p.Key == Loose)
            .ThenBy(p => features[p.Key].Source.Path, StringComparer.Ordinal).ThenBy(p => features[p.Key].Source.Line)
            .Select(p => Get("feature:" + p.Key)).ToList();
        if (documents.Count > 0) roots.Add(Get("files:all"));
        Roots = roots;
        var aggregate = new Dictionary<(string From, string To), int>();
        foreach (CodeRelation edge in report.Graph.Relations)
        {
            if (!relations.TryGetValue(edge.From, out List<CodeRelation>? outgoing)) relations[edge.From] = outgoing = [];
            outgoing.Add(edge);
            if (edge.From != edge.To) { if (!relations.TryGetValue(edge.To, out List<CodeRelation>? incoming)) relations[edge.To] = incoming = []; incoming.Add(edge); }
            if (!symbols.TryGetValue(edge.From, out CodeNode? from) || !symbols.TryGetValue(edge.To, out CodeNode? to)) continue;
            string a = RootFeature(from.Feature), b = RootFeature(to.Feature);
            if (a != b) aggregate[(a, b)] = aggregate.GetValueOrDefault((a, b)) + 1;
        }
        ModuleLinks = aggregate.Select(p => new ModuleLink("feature:" + p.Key.From, "feature:" + p.Key.To, p.Value)).ToArray();
    }

    /// <summary>解析功能 ID、符号 ID 或界面节点 ID；不接受模糊名称匹配。</summary>
    public ExplorerItem Resolve(string id)
    {
        if (features.ContainsKey(id)) id = "feature:" + id;
        else if (symbols.ContainsKey(id)) id = "node:" + id;
        return Get(id);
    }

    /// <summary>按用途、名称、完整标识和源码路径查找，结果有明确上限。</summary>
    public IReadOnlyList<ExplorerItem> Search(string query, int limit = 100)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        limit = Math.Clamp(limit, 1, 500);
        var result = new List<ExplorerItem>();
        bool Match(params string[] values) => values.Any(v => v.Contains(query, StringComparison.OrdinalIgnoreCase));
        foreach (FeatureDefinition feature in features.Values)
        {
            if (Match(feature.Id, feature.Name, feature.Purpose, feature.Source.Path)) result.Add(Get("feature:" + feature.Id));
            if (result.Count >= limit) return result;
        }
        foreach (CodeNode symbol in symbols.Values)
        {
            if (Match(symbol.Name, symbol.Symbol, symbol.Summary, symbol.Source.Path)) result.Add(Get("node:" + symbol.Id));
            if (result.Count >= limit) break;
        }
        foreach (SourceDocument document in documents.Values)
        {
            if (result.Count >= limit) break;
            if (Match(document.Path)) result.Add(Get("file:" + document.Path));
        }
        return result;
    }

    /// <summary>从报告原文取源码行；报告中的路径不会触发任意本地文件读取。</summary>
    public string SourceText(ExplorerItem item, int context = 14)
    {
        if (item.Source is not { } location || !documents.TryGetValue(location.Path, out SourceDocument? document))
            return "报告中没有此节点的源码原文。";
        string[] lines = document.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        int start = Math.Clamp(location.Line - 4, 0, Math.Max(0, lines.Length - 1));
        int end = Math.Min(lines.Length, Math.Max(location.Line, start) + Math.Clamp(context, 1, 200));
        return string.Join(Environment.NewLine, Enumerable.Range(start, end - start).Select(i => $"{i + 1,5}  {lines[i]}"));
    }

    /// <summary>返回与所选符号直接相关的静态关系，避免一次输出整张图。</summary>
    public IReadOnlyList<CodeRelation> Relations(ExplorerItem item, int limit = 100)
    {
        string id = item.Id.StartsWith("node:", StringComparison.Ordinal) ? item.Id[5..] : "";
        return relations.TryGetValue(id, out List<CodeRelation>? found) ? found.Take(Math.Clamp(limit, 1, 500)).ToArray() : [];
    }

    /// <summary>取得已加载对象；同一报告中的相同 ID 始终返回同一实例。</summary>
    private ExplorerItem Get(string key)
    {
        if (items.TryGetValue(key, out ExplorerItem? cached)) return cached;
        Func<IReadOnlyList<ExplorerItem>>? loader = children.ContainsKey(key) ? () => children[key].Select(Get).ToArray() : null;
        ExplorerItem item;
        string? parent;
        if (key.StartsWith("feature:", StringComparison.Ordinal) && features.TryGetValue(key[8..], out FeatureDefinition? f))
        {
            item = new(key, f.Name, f.Purpose, f.Kind is FeatureKind.Module or FeatureKind.Shared ? "模块" : "子功能", source: f.Source,
                inputs: f.Inputs, outputs: f.Outputs, children: loader);
            parent = featureParents[f.Id] is { } p ? "feature:" + p : null;
        }
        else if (key.StartsWith("node:", StringComparison.Ordinal) && symbols.TryGetValue(key[5..], out CodeNode? n))
        {
            item = new(key, n.Name, string.IsNullOrWhiteSpace(n.Summary) ? "缺少用途说明，请补充 XML summary。" : n.Summary,
                n.Kind, RoleLabel(n.Role), n.Source, n.Inputs, n.Outputs, n.Effect == EffectKind.Unspecified ? "作用尚未声明" : n.Effect.ToString(), loader, n.DeclarationBasis);
            parent = symbolParents.GetValueOrDefault(key);
        }
        else if (groupLabels.TryGetValue(key, out string? label))
        {
            item = new(key, label, "只显示当前功能中的这一职责，界面与实现分别组织。", "职责", children: loader);
            parent = symbolParents[key];
        }
        else if (contexts.TryGetValue(key, out CodeNode? owner))
        {
            item = new(key, owner.Name, "当前功能在此类型中的成员；其他成员仍保留原归属。", owner.Kind, RoleLabel(owner.Role), owner.Source, children: loader);
            parent = symbolParents[key];
        }
        else if (key == "files:all")
        {
            item = new(key, "源码与分析盲区", $"保留全部 {documents.Count} 个文件原文，包括暂不支持语义解析的文件。", "覆盖范围",
                children: () => documents.Keys.Order(StringComparer.Ordinal).Select(p => Get("file:" + p)).ToArray());
            parent = null;
        }
        else if (key.StartsWith("file:", StringComparison.Ordinal) && documents.TryGetValue(key[5..], out SourceDocument? document))
        {
            item = new(key, document.Path, document.Language == ".cs" ? "C# 源文件原文。" : "暂不支持此语言的语义解析；原文仍可查看。",
                document.Language, source: new(document.Path, 1, 1));
            parent = "files:all";
        }
        else throw new KeyNotFoundException("未找到代码元素：" + key);
        items.Add(key, item);
        if (parent is not null) item.Parent = Get(parent);
        return item;
    }

    /// <summary>把直接子关系纳入索引。</summary>
    private void AddChild(string parent, string child)
    {
        if (!children.TryGetValue(parent, out List<string>? list)) children[parent] = list = [];
        list.Add(child);
    }

    /// <summary>沿已校验的父级找到根模块。</summary>
    private string RootFeature(string id)
    {
        while (featureParents.TryGetValue(id, out string? parent) && parent is not null) id = parent;
        return id;
    }

    /// <summary>把技术职责转换为面向使用者的名称。</summary>
    public static string RoleLabel(ArchitecturalRole? role) => role switch
    {
        ArchitecturalRole.Feature => "功能实现", ArchitecturalRole.Presentation => "界面与状态",
        ArchitecturalRole.Contract => "接口与契约", ArchitecturalRole.DataAccess => "数据访问 / CRUD",
        ArchitecturalRole.Application => "用例编排", ArchitecturalRole.Integration => "外部适配",
        ArchitecturalRole.Composition => "注册与组装", ArchitecturalRole.Framework => "声明框架",
        ArchitecturalRole.Test => "回归验证", _ => "未声明职责"
    };
}

/// <summary>两个根模块之间已解析静态关系的聚合数量。</summary>
public sealed class ModuleLink(string from, string to, int count)
{
    public string From { get; } = from;
    public string To { get; } = to;
    public int Count { get; } = count;
}
