using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Basalt.Common;

/// <summary>
/// 原生 Windows 图标提取。
/// <para>
/// VHD 模式：文件/文件夹在真实磁盘上，用 ExtractAssociatedIcon 取关联程序图标。
/// VMDK 模式：文件只存在于镜像内，没有真实路径 —— 改用
/// SHGetFileInfo + SHGFI_USEFILEATTRIBUTES，按扩展名从系统注册表取关联图标
/// （该标志允许目标文件不存在）。
/// </para>
/// <para>
/// 提取到的 HICON 是非托管资源，必须显式 DestroyIcon 销毁，否则句柄泄漏。
/// 转成 BitmapSource 后 Freeze，可安全跨线程使用。
/// </para>
/// </summary>
public static class IconProvider
{
    private const uint SHGFI_USEFILEATTRIBUTES = 0x00000010;
    private const uint SHGFI_ICON = 0x00000100;
    private const uint SHGFI_LARGEICON = 0x00000000;
    private const uint SHGFI_SMALLICON = 0x00000001;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, IntPtr ppvInfo, uint cbInfo, uint uFlags);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr ExtractAssociatedIcon(IntPtr hInst, string lpIconName, ref short piIconIndex);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    private static ImageSource? _folderIcon;

    /// <summary>按扩展名缓存图标，避免同一目录里大量同类型文件反复提取。</summary>
    private static readonly Dictionary<string, ImageSource?> ExtensionCache = new(StringComparer.OrdinalIgnoreCase);

    private static readonly object Gate = new();

    /// <summary>取真实磁盘文件的关联图标；path 不存在时返回 null。</summary>
    public static ImageSource? FromRealPath(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            short index = 0;
            IntPtr hIcon = ExtractAssociatedIcon(IntPtr.Zero, path, ref index);
            return ConvertAndRelease(hIcon);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 系统文件夹图标（共享实例）。
    /// 取法：SHGetFileInfo 指向一个真实存在的目录（如 C:\Windows）—— 系统按目录类型返回文件夹图标。
    /// </summary>
    public static ImageSource? FolderIcon
    {
        get
        {
            if (_folderIcon != null) return _folderIcon;
            lock (Gate)
            {
                if (_folderIcon != null) return _folderIcon;
                try
                {
                    int size = Marshal.SizeOf<SHFILEINFO>();
                    IntPtr buf = Marshal.AllocHGlobal(size);
                    try
                    {
                        string anyDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                        SHGetFileInfo(anyDir, 0, buf, (uint)size, SHGFI_ICON | SHGFI_LARGEICON);
                        var info = Marshal.PtrToStructure<SHFILEINFO>(buf);
                        _folderIcon = ConvertAndRelease(info.hIcon);
                    }
                    finally { Marshal.FreeHGlobal(buf); }
                }
                catch { /* 取不到就退回 null */ }
                return _folderIcon;
            }
        }
    }

    /// <summary>
    /// 按扩展名取系统关联图标（VMDK 场景用）。ext 形如 ".docx"；无关联则返回 null。
    /// </summary>
    public static ImageSource? FromExtension(string? ext)
    {
        if (string.IsNullOrEmpty(ext)) return null;
        lock (Gate)
        {
            if (ExtensionCache.TryGetValue(ext, out var cached)) return cached;
            ImageSource? result = null;
            try
            {
                int size = Marshal.SizeOf<SHFILEINFO>();
                IntPtr buf = Marshal.AllocHGlobal(size);
                try
                {
                    // SHGFI_USEFILEATTRIBUTES：即使文件不存在也能按扩展名取到关联图标
                    string fake = Path.Combine(Path.GetTempPath(), $"__basalt_icon_probe{ext}");
                    uint flags = SHGFI_ICON | SHGFI_SMALLICON | SHGFI_USEFILEATTRIBUTES;
                    SHGetFileInfo(fake, 0, buf, (uint)size, flags);
                    var info = Marshal.PtrToStructure<SHFILEINFO>(buf);
                    result = ConvertAndRelease(info.hIcon);
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            catch { /* 取不到就退回 null */ }
            ExtensionCache[ext] = result;
            return result;
        }
    }

    /// <summary>HICON → BitmapSource，并销毁非托管句柄（避免句柄泄漏）。</summary>
    private static ImageSource? ConvertAndRelease(IntPtr hIcon)
    {
        if (hIcon == IntPtr.Zero) return null;
        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(
                hIcon,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            source.Freeze(); // 冻结后可安全跨线程使用（WPF 要求）
            return source;
        }
        catch
        {
            return null;
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }
}
