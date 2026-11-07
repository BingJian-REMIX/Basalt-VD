using System.IO;
using System.Text;

namespace Basalt.Services;

/// <summary>CLI daemon 的停止信号通道自测（不依赖管理员挂载权限）。</summary>
public static class CliSelfTest
{
    public static string Run()
    {
        var sb = new StringBuilder();
        var failures = new List<string>();
        void Check(bool ok, string label, string detail = "")
        {
            sb.AppendLine($"   {(ok ? "✅" : "❌")} {label}{(detail.Length > 0 ? " — " + detail : "")}");
            if (!ok) failures.Add(label);
        }

        sb.AppendLine("=== Basalt CLI daemon 自测 ===");
        sb.AppendLine("---- loopback TCP 停止信号 ----");

        using var stop = new ManualResetEventSlim(false);
        var listener = CliMount.StartListener(0, "unit-token", stop, out int port);
        try
        {
            Check(port > 0, "临时端口监听成功", $"127.0.0.1:{port}");
            int code = CliMount.RunStop(new[] { "stop", "--port", port.ToString(), "--token", "unit-token" });
            Check(code == 0, "正确 token 返回 OK", $"exit={code}");
            Check(stop.Wait(3000), "收到 STOP 信号（会触发 DetachVirtualDisk）");
        }
        finally { listener.Stop(); }

        sb.AppendLine("---- 错误 token 拒绝 ----");
        using var stop2 = new ManualResetEventSlim(false);
        var listener2 = CliMount.StartListener(0, "unit-token", stop2, out int port2);
        try
        {
            int code = CliMount.RunStop(new[] { "stop", "--port", port2.ToString(), "--token", "wrong-token" });
            Check(code == 1 && !stop2.Wait(500), "错误 token 被拒绝且不触发卸载", $"exit={code}");
        }
        finally { listener2.Stop(); }

        sb.AppendLine("---- 参数与失败路径 ----");
        Check(CliMount.RunMount(new[] { "mount" }) == 2, "缺少路径 → 用法提示");
        Check(CliMount.RunMount(new[] { "mount", Path.Combine(Path.GetTempPath(), "no_such_disk.vhdx"), "--silent", "--daemon" }) == 2,
            "文件不存在 → 立即失败不残留 daemon");

        sb.AppendLine();
        sb.AppendLine(failures.Count == 0
            ? "=== 全部通过 PASS ==="
            : $"=== 失败 {failures.Count} 项 FAIL: {string.Join(" / ", failures)} ===");
        return sb.ToString();
    }
}
