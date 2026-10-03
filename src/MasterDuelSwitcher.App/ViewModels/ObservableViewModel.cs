using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MasterDuelSwitcher.App.ViewModels;

/// <summary>提供各页共用的属性通知和集合更新，不承载业务操作。</summary>
public abstract class ObservableViewModel : INotifyPropertyChanged
{
    /// <summary>通知视图重新读取发生变化的属性。</summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>仅在属性值改变时保存新值并通知绑定。</summary>
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string name = "")
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Notify(name);
        return true;
    }

    /// <summary>通知一组依赖当前状态的派生属性。</summary>
    protected void Notify(params string[] names)
    {
        foreach (var name in names) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>先物化输入再替换集合，保留使用旧集合进行筛选的行为。</summary>
    protected static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> values)
    {
        var snapshot = values.ToArray();
        target.Clear();
        foreach (var item in snapshot) target.Add(item);
    }

    /// <summary>将空白的安装偏好转换为自动发现参数。</summary>
    public static string? EmptyToNull(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
