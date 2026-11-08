using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Basalt.Services;

/// <summary>
/// CLI 自动化接口（供 Chert 后台调用）：
///   basalt.exe mount "C:\test.vhdx" --readonly --silent --daemon [--port N]
///   basalt.exe stop [--port N] [--token T]
/// <para>
/// --daemon 时主线程阻塞休眠保持进程存活，挂载盘符持续存在；
/// 收到控制台输入 "stop" 或 loopback TCP 的 "STOP &lt;token&gt;" 才 DetachVirtualDisk 并退出。
/// 状态（端口 / token / 盘符）写入 %TEMP%\basalt_mount_state.json，供调用方与 stop 子命令读取。
/// Chert 只需 Process.Start 拉起，不必等待进程结束。
/// </para>
/// </summary>
public static class CliMount
{
    private const int DefaultPort = 47821;
    private const uint ATTACH_PARENT_PROCESS = 0xFFFFFFFF;

    public static string StateFile => Path.Combine(Path.GetTempPath(), "basalt_mount_state.json");
    private static string LogFile => Path.Combine(Path.GetTempPath(), "basalt_cli.log");

    private sealed class State
    {
        public int Pid { get; set; }
        public int Port { get; set; }
        public string Token { get; set; } = "";
        public string Drive { get; set; } = "";
        public string Path { get; set; } = "";
        public bool ReadOnly { get; set; }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(uint dwProcessId);

    /// <summary>同时写父控制台（若存在）与临时日志；静默模式下不弹任何窗口。</summary>
    private static void Out(string message)
    {
        try { AttachConsole(ATTACH_PARENT_PROCESS); Console.WriteLine(message); } catch { /* 无父控制台 */ }
        try { File.AppendAllText(LogFile, $"[{DateTime.Now:O}] {message}\n"); } catch { }
    }

    public static int RunMount(string[] args)
    {
        string? path = null;
        bool readOnly = false, silent = false, daemon = false;
        int port = DefaultPort;

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a.Equals("mount", StringComparison.OrdinalIgnoreCase)) continue;
            else if (a is "--readonly") readOnly = true;
            else if (a is "--silent") silent = true;
            else if (a is "--daemon") daemon = true;
            else if (a is "--port" && i + 1 < args.Length && int.TryParse(args[++i], out int p)) port = p;
            else if (!a.StartsWith("--", StringComparison.Ordinal) && path == null) path = a;
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            Out("用法: basalt.exe mount <VHD/VHDX> [--readonly] [--silent] [--daemon] [--port N]");
            return 2;
        }
        if (!File.Exists(path))
        {
            Out($"找不到虚拟磁盘文件：{path}");
            return 2;
        }

        MountService? mount = null;
        try
        {
            mount = new MountService();
            char letter = mount.MountVHD(path, readOnly);
            string drive = $"{letter}:\\";
            Out($"MOUNTED {drive} {path} readonly={readOnly} silent={silent} daemon={daemon}");

            // 无 --daemon：只做挂载预检，随即卸载退出（调用方不阻塞）
            if (!daemon)
            {
                mount.Dispose();
                mount = null;
                Out("PREFLIGHT OK (detached)");
                return 0;
            }

            var state = new State
            {
                Pid = Environment.ProcessId,
                Port = port,
                Token = Guid.NewGuid().ToString("N"),
                Drive = drive,
                Path = Path.GetFullPath(path),
                ReadOnly = readOnly
            };

            using var stop = new ManualResetEventSlim(false);
            var listener = StartListener(port, state.Token, stop, out int actualPort);
            state.Port = actualPort;
            File.WriteAllText(StateFile, JsonSerializer.Serialize(state));
            Out($"DAEMON READY port={actualPort} token={state.Token} state={StateFile}");

            StartStdinWatcher(stop);
            stop.Wait();                 // 主线程阻塞休眠：进程不退出 → 盘符一直保留

            listener.Stop();
            Out("STOP received, detaching ...");
            mount.Dispose();             // DetachVirtualDisk
            mount = null;
            try { File.Delete(StateFile); } catch { }
            Out("DETACHED, exiting.");
            return 0;
        }
        catch (Exception ex)
        {
            Out($"MOUNT FAILED: {ex.Message}");
            try { mount?.Dispose(); } catch { }
            return 1;
        }
    }

    /// <summary>向常驻的 Basalt 挂载进程发送停止信号。</summary>
    public static int RunStop(string[] args)
    {
        int port = 0;
        string? token = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] is "--port" && i + 1 < args.Length) int.TryParse(args[++i], out port);
            else if (args[i] is "--token" && i + 1 < args.Length) token = args[++i];
        }

        State? state = null;
        try
        {
            if (File.Exists(StateFile))
                state = JsonSerializer.Deserialize<State>(File.ReadAllText(StateFile));
        }
        catch { /* 状态文件损坏 */ }

        if (port == 0) port = state?.Port ?? 0;
        token ??= state?.Token;
        if (port == 0 || string.IsNullOrEmpty(token))
        {
            Out("没有可用的挂载状态，且未指定 --port / --token。");
            return 2;
        }

        try
        {
            using var client = new TcpClient();
            client.Connect(IPAddress.Loopback, port);
            using var stream = client.GetStream();
            using var writer = new StreamWriter(stream, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(stream, Encoding.UTF8, false, 1024, leaveOpen: true);
            writer.WriteLine($"STOP {token}");
            string? reply = reader.ReadLine();
            Out($"STOP -> 127.0.0.1:{port} : {reply}");
            return reply == "OK" ? 0 : 1;
        }
        catch (Exception ex)
        {
            Out($"STOP FAILED: {ex.Message}");
            return 1;
        }
    }

    internal static TcpListener StartListener(int port, string token, ManualResetEventSlim stop, out int actualPort)
    {
        for (int i = 0; i < 10; i++)
        {
            int candidate = port + i;
            var listener = new TcpListener(IPAddress.Loopback, candidate);
            try { listener.Start(); }
            catch (SocketException) { continue; }

            // port=0 时由系统分配临时端口，必须从 LocalEndpoint 取真实值
            actualPort = ((IPEndPoint)listener.LocalEndpoint).Port;
            _ = Task.Run(async () =>
            {
                try
                {
                    while (!stop.IsSet)
                    {
                        var client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
                        _ = Task.Run(() => HandleClient(client, token, stop));
                    }
                }
                catch (SocketException) { }
                catch (ObjectDisposedException) { }
            });
            return listener;
        }
        throw new InvalidOperationException($"无法在 127.0.0.1:{port}~{port + 9} 上建立停止信号监听。");
    }

    private static void HandleClient(TcpClient client, string token, ManualResetEventSlim stop)
    {
        using (client)
        {
            try
            {
                client.ReceiveTimeout = 5000;
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.UTF8, false, 1024, leaveOpen: true);
                using var writer = new StreamWriter(stream, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
                string? line = reader.ReadLine();
                bool ok = line != null && line.Trim().Equals($"STOP {token}", StringComparison.Ordinal);
                writer.WriteLine(ok ? "OK" : "DENIED");
                if (ok) stop.Set();
            }
            catch { /* 客户端断开 */ }
        }
    }

    private static void StartStdinWatcher(ManualResetEventSlim stop)
    {
        _ = Task.Run(() =>
        {
            try
            {
                string? line;
                while (!stop.IsSet && (line = Console.In.ReadLine()) != null)
                {
                    if (line.Trim().Equals("stop", StringComparison.OrdinalIgnoreCase))
                    {
                        Out("STOP received from console.");
                        stop.Set();
                        return;
                    }
                }
            }
            catch { /* 无控制台 / 输入被关闭 */ }
        });
    }
}
