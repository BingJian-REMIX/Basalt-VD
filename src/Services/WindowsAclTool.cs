using System.Diagnostics;
using System.IO;

namespace Basalt.Services;

/// <summary>
/// 处理 Windows 系统映像文件上的 TrustedInstaller ACL。
/// <para>
/// wimlib apply 会原样还原映像里的安全描述符：系统文件的 owner 是
/// <c>NT SERVICE\TrustedInstaller</c>，ACL 只给 Administrators / Users 授予
/// <c>ReadAndExecute</c>。因此即使清掉 <c>ReadOnly</c> 属性，
/// <c>File.SetAttributes</c> 与 <c>File.Delete</c> 仍会抛
/// <c>UnauthorizedAccessException</c>（真实 Win11 实跑里是 2 943 个文件）。
/// </para>
/// <para>
/// 这里用 <c>takeown</c> + <c>icacls</c> 夺权授权。只作用于两类「即将删除」的临时副本：
/// 精简目标目录（删除后不会进入新 WIM）与 capture 之后的工作目录（输出 WIM 已定型）。
/// 被保留文件的 ACL 完全不动，输出映像的安全描述符保持原样。
/// 应用 manifest 声明了 <c>requireAdministrator</c>，因此具备所需特权。
/// </para>
/// </summary>
public static class WindowsAclTool
{
    // BUILTIN\Administrators 的 SID，避免本地化组名带来的兼容问题
    private const string AdminsSid = "*S-1-5-32-544";

    /// <summary>夺权并授权，使路径可被当前提权进程删除。目录递归、文件单独处理。</summary>
    public static void EnsureDeletable(string path)
    {
        bool isDir = Directory.Exists(path);
        if (!isDir && !File.Exists(path)) return;

        // 统一用 /R /D Y：/D 只有配合 /R 才合法，单文件分支早期漏掉 /R 时
        // takeown 会直接参数报错，导致 9 个散落的 SecurityHealth* 文件仍被拒绝删除。
        // /R 作用于单文件时只是「无子项可递归」，不会报错。
        RunQuiet("takeown.exe", new[] { "/F", path, "/R", "/D", "Y" });
        RunQuiet("icacls.exe", isDir
            ? new[] { path, "/grant", AdminsSid + ":(F)", "/T", "/C", "/Q" }
            : new[] { path, "/grant", AdminsSid + ":(F)", "/C", "/Q" });
    }

    /// <summary>批量夺权；调用方应只传入真正删除失败的目录，避免对每个文件起两次进程。</summary>
    public static void EnsureDeletable(IEnumerable<string> paths)
    {
        foreach (string p in paths) EnsureDeletable(p);
    }

    /// <summary>静默运行权限工具，输出全部丢弃（几万行刷屏会拖慢并污染日志）。</summary>
    internal static void RunQuiet(string exe, IEnumerable<string> arguments)
    {
        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (string a in arguments) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p == null) return;
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            p.WaitForExit();
            _ = stdout.Result;
            _ = stderr.Result;
        }
        catch { /* 工具不可用 / 权限不足时由后续删除逻辑兜底 */ }
    }
}
