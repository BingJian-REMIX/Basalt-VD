using Basalt.Common;

namespace Basalt.Models;

/// <summary>右侧文件列表项。</summary>
public sealed class FileItem
{
    public required string Name { get; init; }
    public required string FullPath { get; init; }
    public string SizeText { get; init; } = "";
    /// <summary>字节数（状态栏汇总用；目录与 ".." 为 0）。</summary>
    public long Size { get; init; }
    public string ModifiedText { get; init; } = "";
    public string CreatedText { get; init; } = "";

    /// <summary>原生 Windows 图标（ImageSource）。由 IconProvider 填充，失败时为 null。</summary>
    public System.Windows.Media.ImageSource? IconSource { get; init; }

    /// <summary>是否为目录（决定双击行为：进目录 vs 打开文件）。</summary>
    public bool IsDirectory { get; init; }

    /// <summary>是否是".." 返回上级项（VHD 根目录、VMDK 镜像根下没有该项）。</summary>
    public bool IsParentEntry { get; init; }

    /// <summary>
    /// 该".." 是否表示"返回上一个磁盘"（在容器根目录时）。
    /// 容器展开后，根目录的 ".." 应退回原先的虚拟磁盘，而不是留在容器里。
    /// </summary>
    public bool PopsDiskStack { get; init; }

    /// <summary>文件属性（只读/隐藏/存档…），供属性窗口展示。</summary>
    public string AttributesText { get; init; } = "";
}

/// <summary>文件元数据详情（属性窗口用）。</summary>
public sealed class FileMetadata
{
    public required string Name { get; init; }
    public required string FullPath { get; init; }
    public string SizeText { get; init; } = "";
    public long Size { get; init; }
    public string CreatedText { get; init; } = "";
    public string ModifiedText { get; init; } = "";
    public string AccessedText { get; init; } = "";
    public string AttributesText { get; init; } = "";
    public bool IsDirectory { get; init; }
}

/// <summary>大小/时间格式化（VHD / VMDK 两个引擎共用）。</summary>
public static class FileItemFormatter
{
    public static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):F2} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):F2} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):F2} KB",
        _ => $"{bytes} B"
    };

    public static string FormatTime(DateTime t) =>
        t == default ? "—" : t.ToString("yyyy-MM-dd HH:mm:ss");
}

/// <summary>左侧目录树节点（懒加载子目录，加载器由当前引擎注入）。</summary>
public sealed class FolderNode : ViewModels.ViewModelBase
{
    /// <summary>当前挂载引擎的目录枚举器，由 MainViewModel 在挂载后设置。</summary>
    public static Func<string, List<FolderNode>>? ChildLoader { get; set; }

    private bool _isExpanded;
    private bool _isSelected;
    private bool _loaded;

    public required string Name { get; init; }
    public required string FullPath { get; init; }

    /// <summary>原生 Windows 文件夹图标。</summary>
    public System.Windows.Media.ImageSource? IconSource { get; set; }

    // 用 RangeObservableCollection（ObservableCollection 子类）：展开节点时把子目录整体灌入，
    // 既能触发 CollectionChanged 让 TreeView 立即刷新，又只发一次 Reset 通知（不卡 UI）。
    // 之前用普通 List<T> 不会触发任何通知，子目录要等调整窗口大小才显示。
    public RangeObservableCollection<FolderNode> Children { get; } = new();

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (Set(ref _isExpanded, value) && value)
                LoadChildren();
        }
    }

    /// <summary>与 TreeViewItem.IsSelected 双向绑定：点击节点即选中并高亮。</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }

    /// <summary>
    /// 懒加载子目录（只加载一次）。在 IsExpanded 变为 true 时调用；
    /// 选中节点时也会调用一次作为兜底 —— 某些情况下 TreeViewItem 不会把展开状态写回绑定源，
    /// 若只依赖 IsExpanded，展开箭头会出来但子节点永远为空。
    /// </summary>
    public void LoadChildren()
    {
        if (_loaded) return;
        _loaded = true;
        var loader = ChildLoader;
        if (loader == null) return;
        try
        {
            Children.Reset(loader(FullPath));
        }
        catch { /* 无权限/坏扇区时静默 */ }
    }
}
