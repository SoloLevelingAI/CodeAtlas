using System.Windows.Controls;

namespace CodeAtlas.Desktop;

/// <summary>让命令行定位能够实现尚未生成的树节点容器，仍保留树的虚拟化。</summary>
public sealed class AtlasVirtualizingPanel : VirtualizingStackPanel
{
    /// <summary>仅为目标索引实现必要的节点容器。</summary>
    public void Realize(int index) => BringIndexIntoView(index);
}
