using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace Basalt.Common;

/// <summary>
/// 支持"批量重置"的 ObservableCollection。
/// <para>
/// 背景：左侧目录树原先用 List&lt;FolderNode&gt;，挂载后 Roots.Add 不会触发任何通知，
/// TreeView 直到调整窗口大小被迫重排才显示（用户反馈"要调整窗口大小才显示"）。
/// 换成 ObservableCollection 即可解决，但它只有 Add 没有 AddRange，且逐条 Add 会
/// 触发 N 次 CollectionChanged，在目录里有几千个文件时 UI 会明显卡顿。
/// </para>
/// <para>
/// 本类在 Reset 期间抑制中间事件，最后只发一次 Reset 通知，兼顾正确性与性能。
/// </para>
/// </summary>
public class RangeObservableCollection<T> : ObservableCollection<T>
{
    private bool _suppressEvents;

    protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
    {
        if (_suppressEvents) return;
        base.OnCollectionChanged(e);
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        if (_suppressEvents) return;
        base.OnPropertyChanged(e);
    }

    /// <summary>用新内容整体替换集合，期间不发通知，结束时统一发一次 Reset。</summary>
    public void Reset(IEnumerable<T> items)
    {
        _suppressEvents = true;
        try
        {
            Items.Clear();
            foreach (var item in items) Items.Add(item);
        }
        finally
        {
            _suppressEvents = false;
        }

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
