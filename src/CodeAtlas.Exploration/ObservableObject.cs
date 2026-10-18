using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace CodeAtlas.Exploration;

/// <summary>只为实际变化的属性发出通知，不比较或复制整份报告。</summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>在属性值改变时更新字段并通知订阅者。</summary>
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? property = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Changed(property);
        return true;
    }

    /// <summary>通知指定派生属性需要重新读取。</summary>
    protected void Changed([CallerMemberName] string? property = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}
