using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace Basalt.Services;

/// <summary>
/// 与同门 Chert 启动器的联动。
/// <para>
/// 依据 Claw/MCLCS-WPF-clean 源码（2.6.0）：
/// · 游戏工作目录由 Chert.Core.Utils.GameConstants 从 %APPDATA%\Chert\gameroot.txt 读取，
///   目录不存在时回落 %APPDATA%\.minecraft —— 这里保持完全一致的口径。
/// · GUI（Chert.App / "Chert Launcher.exe"）当前不解析命令行参数；
///   CLI（chert.exe）支持 --game-dir，但按钮以 GUI 优先。
/// · chert:// URL Scheme 在 2.6.0 中尚未实现/注册，留作下一版联动通道。
/// </para>
/// </summary>
public static class ChertIntegration
{
    private const string LauncherName = "Chert";

    /// <summary>Chert 当前生效的 .minecraft 工作目录。</summary>
    public static string GameRoot
    {
        get
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            try
            {
                string cfg = Path.Combine(appData, LauncherName, "gameroot.txt");
                if (File.Exists(cfg))
                {
                    string root = File.ReadAllText(cfg).Trim();
                    if (root.Length > 0 && Directory.Exists(root))
                        return Path.GetFullPath(root);
                }
            }
            catch { /* 配置损坏 → 回落系统默认 */ }
            return Path.Combine(appData, ".minecraft");
        }
    }

    /// <summary>
    /// 导出文件到 Chert 工作目录。
    /// VHD 引擎的 ExtractFile 内部就是 File.Copy；VMDK / 容器由各自引擎先落地再拷贝。
    /// </summary>
    public static string Export(IVirtualDisk disk, string path, string name)
    {
        string root = GameRoot;
        Directory.CreateDirectory(root);
        string dest = Path.Combine(root, name);
        disk.ExtractFile(path, dest);
        return dest;
    }

    /// <summary>
    /// 「在 Chert 中启动」：GUI 优先。
    /// 仅当 chert:// 已注册（下一版 Chert）时按规格走 URL Scheme；
    /// 否则直接拉起 GUI 并附 --game-dir 预留参数，当前版本忽略该参数但不影响启动。
    /// </summary>
    public static string Launch(string? gameRoot)
    {
        string root = string.IsNullOrWhiteSpace(gameRoot) ? GameRoot : gameRoot!;

        if (SchemeRegistered())
        {
            string url = $"chert://launch?path={Uri.EscapeDataString(root)}&type=minecraft";
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                return $"已通过 chert:// 唤起 Chert — {root}";
            }
            catch { /* 已注册但处理失败 → 落到 GUI */ }
        }

        string? gui = FindGui();
        if (gui == null)
            return "未找到 Chert GUI（Chert Launcher.exe / Chert.App.exe）；可用 BASALT_CHERT_GUI 环境变量指定路径。";

        var psi = new ProcessStartInfo(gui)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(gui) ?? AppContext.BaseDirectory
        };
        psi.ArgumentList.Add("--game-dir");
        psi.ArgumentList.Add(root);
        psi.ArgumentList.Add("--type=minecraft");
        Process.Start(psi);

        return $"已启动 Chert GUI（{Path.GetFileName(gui)}）— {root}；chert:// 将在下一版 Chert 中支持";
    }

    private static bool SchemeRegistered()
    {
        try
        {
            using var hkcr = Registry.ClassesRoot.OpenSubKey("chert");
            if (hkcr?.GetValue("URL Protocol") != null) return true;
            using var hkcu = Registry.CurrentUser.OpenSubKey(@"Software\Classes\chert");
            return hkcu?.GetValue("URL Protocol") != null;
        }
        catch { return false; }
    }

    /// <summary>定位 Chert GUI 可执行文件；优先环境变量覆盖。</summary>
    private static string? FindGui()
    {
        string? fromEnv = Environment.GetEnvironmentVariable("BASALT_CHERT_GUI");
        if (!string.IsNullOrWhiteSpace(fromEnv) && File.Exists(fromEnv)) return fromEnv;

        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var bases = new[]
        {
            AppContext.BaseDirectory,
            Path.Combine(localAppData, "Programs", "Chert"),
            Path.Combine(appData, "Chert"),
            @"D:\data\work\Chert-out",
            @"D:\data\work"
        };
        var names = new[] { "Chert Launcher.exe", "Chert.App.exe", "chert launcher.exe" };

        foreach (var dir in bases)
            foreach (var name in names)
            {
                string p = Path.Combine(dir, name);
                if (File.Exists(p)) return p;
            }
        return null;
    }
}
