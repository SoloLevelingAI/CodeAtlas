using System.Collections.ObjectModel;
using CodeAtlas.Contracts;

namespace CodeAtlas.Exploration;

/// <summary>界面中的稳定节点对象；子项按展开需求生成，节点身份始终不变。</summary>
public sealed class ExplorerItem : ObservableObject
{
    private readonly Func<IReadOnlyList<ExplorerItem>>? loadChildren;
    private bool expanded;
    private bool selected;
    private bool current;
    public string Id { get; }
    public string Title { get; }
    public string Summary { get; }
    public string Kind { get; }
    public string Role { get; }
    public SourceLocation? Source { get; }
    public string Inputs { get; }
    public string Outputs { get; }
    public string Effect { get; }
    public string Basis { get; }
    public ExplorerItem? Parent { get; internal set; }
    public ObservableCollection<ExplorerItem> Children { get; } = [];
    public bool HasChildren { get; }
    public bool IsLoaded { get; private set; }
    public bool IsExpanded { get => expanded; set { if (Set(ref expanded, value) && value) EnsureChildren(); } }
    public bool IsSelected { get => selected; set => Set(ref selected, value); }
    public bool IsCurrent { get => current; internal set => Set(ref current, value); }
    public string SourceLabel => Source is null ? "无源码位置" : $"{Source.Path}:{Source.Line}";

    /// <summary>创建展示节点；加载器只生成当前节点的直接子项。</summary>
    public ExplorerItem(string id, string title, string summary, string kind, string role = "",
        SourceLocation? source = null, string inputs = "", string outputs = "", string effect = "",
        Func<IReadOnlyList<ExplorerItem>>? children = null, string basis = "")
    {
        Id = id; Title = title; Summary = summary; Kind = kind; Role = role;
        Source = source; Inputs = inputs; Outputs = outputs; Effect = effect; Basis = basis;
        loadChildren = children; HasChildren = children is not null;
        // A placeholder lets the virtualized TreeView show an expander before materializing children.
        if (HasChildren) Children.Add(new ExplorerItem("placeholder:" + id, "展开子项", "", "占位"));
    }

    /// <summary>仅第一次展开时载入子项；随后复用同一组对象。</summary>
    public void EnsureChildren()
    {
        if (IsLoaded) return;
        IsLoaded = true;
        Children.Clear();
        foreach (ExplorerItem child in loadChildren?.Invoke() ?? [])
        {
            child.Parent = this;
            Children.Add(child);
        }
    }
}
