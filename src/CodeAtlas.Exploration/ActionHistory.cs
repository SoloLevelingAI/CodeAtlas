namespace CodeAtlas.Exploration;

/// <summary>有容量上限的动作历史；每步只保存小范围状态变更，不保留图表副本。</summary>
public sealed class ActionHistory(int capacity = 150)
{
    private sealed class Change(Action undo, Action redo)
    {
        public Action Undo { get; } = undo;
        public Action Redo { get; } = redo;
    }
    private readonly List<Change> entries = [];
    private int position;
    public bool CanUndo => position > 0;
    public bool CanRedo => position < entries.Count;
    public int Count => entries.Count;

    /// <summary>执行新动作并截断已撤销的分支；超过容量时淘汰最早步骤。</summary>
    public void Execute(Action undo, Action redo)
    {
        redo();
        if (position < entries.Count) entries.RemoveRange(position, entries.Count - position);
        entries.Add(new Change(undo, redo));
        if (entries.Count > Math.Max(1, capacity)) entries.RemoveAt(0);
        position = entries.Count;
    }

    /// <summary>撤销最后一步；没有历史时不执行操作。</summary>
    public void Undo() { if (CanUndo) entries[--position].Undo(); }
    /// <summary>重做已撤销的下一步。</summary>
    public void Redo() { if (CanRedo) entries[position++].Redo(); }
    /// <summary>报告替换时释放旧报告关联的动作引用。</summary>
    public void Clear() { entries.Clear(); position = 0; }
}
