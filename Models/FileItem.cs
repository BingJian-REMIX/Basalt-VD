using Basalt.Common;

namespace Basalt.Models;

/// <summary>右侧文件列表项。</summary>
public sealed class FileItem
{
    public required string Name { get; init; }
    public required string FullPath { get; init; }
    public string SizeText { get; init; } = "";
    public string ModifiedText { get; init; } = "";
    public string Icon { get; init; } = "📄";
}

/// <summary>大小/图标的统一格式化（VHD / VMDK 两个引擎共用）。</summary>
public static class FileItemFormatter
{
    public static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):F2} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):F2} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):F2} KB",
        _ => $"{bytes} B"
    };

    public static string GetIcon(string ext) => ext.ToLowerInvariant() switch
    {
        ".exe" or ".msi" or ".dll" or ".com" or ".sys" => "⚙️",
        ".txt" or ".md" or ".log" or ".ini" or ".cfg" => "📝",
        ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" => "🖼️",
        ".zip" or ".rar" or ".7z" or ".gz" => "📦",
        ".mp3" or ".wav" or ".flac" or ".mid" => "🎵",
        ".mp4" or ".mkv" or ".avi" => "🎬",
        ".doc" or ".docx" => "📘",
        ".xls" or ".xlsx" => "📗",
        ".pdf" => "📕",
        _ => "📄"
    };
}

/// <summary>左侧目录树节点（懒加载子目录，加载器由当前引擎注入）。</summary>
public sealed class FolderNode : ViewModels.ViewModelBase
{
    /// <summary>当前挂载引擎的目录枚举器，由 MainViewModel 在挂载后设置。</summary>
    public static Func<string, List<FolderNode>>? ChildLoader { get; set; }

    private bool _isExpanded;
    private bool _loaded;

    public required string Name { get; init; }
    public required string FullPath { get; init; }

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
