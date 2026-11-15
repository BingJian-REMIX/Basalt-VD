using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Basalt.Services;

public enum WimEngineKind
{
    None,
    Wimlib,
    Dism
}

/// <summary>可用的 WIM 处理引擎。wimlib 优先（无需提权、支持 export 保留其它版本），DISM 作兜底。</summary>
public sealed class WimEngine
{
    public WimEngineKind Kind { get; init; }
    public string ExePath { get; init; } = "";
    public string VersionNote { get; init; } = "";

    public bool Available => Kind != WimEngineKind.None;

    public string Label => Kind switch
    {
        WimEngineKind.Wimlib => "wimlib-imagex（内置 1.14.5）",
        WimEngineKind.Dism => "DISM（系统内置，需要管理员权限）",
        _ => "不可用"
    };
}

/// <summary>
/// WIM 处理工具的统一封装：apply / capture / export 三个原子操作。
/// <para>
/// 优先使用随程序分发的 <c>tools\wimlib-x64\wimlib-imagex.exe</c>（官方二进制，GPLv3+），
/// 它没有管理员权限要求、能逐个 export 保留未精简的版本；找不到时回退到系统 DISM。
/// 两者都通过 <see cref="Run"/> 的同一套进程封装执行：实时读 stdout/stderr、
/// 解析百分比、取消时 Kill 整棵进程树，绝不把输出一次性 WaitForExit 后才处理。
/// </para>
/// </summary>
public static class WimTool
{
    private static readonly Regex PercentRegex = new(@"(?<!\d)(\d{1,3})(?:\.\d+)?\s*%", RegexOptions.Compiled);

    /// <summary>把一行工具输出转成 0~100 的百分比；解析不到返回 null。</summary>
    public static int? ParsePercent(string line)
    {
        var m = PercentRegex.Matches(line);
        if (m.Count == 0) return null;
        if (!int.TryParse(m[^1].Groups[1].Value, out int v)) return null;
        return Math.Clamp(v, 0, 100);
    }

    /// <summary>定位可用引擎：环境变量 → 程序目录 → PATH → 系统 DISM。</summary>
    public static WimEngine Resolve()
    {
        foreach (string? candidate in Candidates())
        {
            if (string.IsNullOrWhiteSpace(candidate) || !File.Exists(candidate)) continue;
            if (!candidate.EndsWith("wimlib-imagex.exe", StringComparison.OrdinalIgnoreCase)) continue;
            if (!File.Exists(Path.Combine(Path.GetDirectoryName(candidate)!, "libwim-15.dll"))) continue;
            return new WimEngine
            {
                Kind = WimEngineKind.Wimlib,
                ExePath = candidate,
                VersionNote = "wimlib 1.14.5 (windows-x86_64)"
            };
        }

        string dism = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "dism.exe");
        if (File.Exists(dism))
            return new WimEngine { Kind = WimEngineKind.Dism, ExePath = dism, VersionNote = "Windows DISM" };

        return new WimEngine { Kind = WimEngineKind.None };
    }

    private static IEnumerable<string?> Candidates()
    {
        yield return Environment.GetEnvironmentVariable("BASALT_WIMLIB");
        yield return Path.Combine(AppContext.BaseDirectory, "tools", "wimlib-x64", "wimlib-imagex.exe");

        string? path = Environment.GetEnvironmentVariable("PATH");
        if (path == null) yield break;
        foreach (string dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate;
            try { candidate = Path.Combine(dir.Trim('"'), "wimlib-imagex.exe"); }
            catch { continue; }   // 非法 PATH 项
            yield return candidate;
        }
    }

    /// <summary>
    /// 原子操作：把 WIM 的某个映像释放到目录。
    /// <c>onLine</c> 会收到工具的每一行输出（含 \r 进度行），调用方负责转成进度。
    /// </summary>
    public static void Apply(WimEngine engine, string wimPath, int index, string targetDir,
        Action<string>? onLine, CancellationToken token)
    {
        Directory.CreateDirectory(targetDir);
        if (engine.Kind == WimEngineKind.Wimlib)
        {
            Run(engine, new[]
            {
                "apply", wimPath, index.ToString(), targetDir, "--norpfix"
            }, onLine, token);
        }
        else
        {
            Run(engine, new[]
            {
                "/Apply-Image", "/ImageFile:" + wimPath, "/Index:" + index,
                "/ApplyDir:" + targetDir, "/NoRestart"
            }, onLine, token);
        }
    }

    /// <summary>原子操作：把目录捕获成新的单映像 WIM（目标文件必须不存在）。</summary>
    public static void Capture(WimEngine engine, string sourceDir, string wimPath,
        string imageName, string description, Action<string>? onLine, CancellationToken token)
    {
        if (File.Exists(wimPath)) File.Delete(wimPath);

        if (engine.Kind == WimEngineKind.Wimlib)
        {
            Run(engine, new[]
            {
                "capture", sourceDir, wimPath, imageName, description,
                "--compress=LZX:50", "--norpfix"
            }, onLine, token);
        }
        else
        {
            Run(engine, new[]
            {
                "/Capture-Image", "/CaptureDir:" + sourceDir, "/ImageFile:" + wimPath,
                "/Name:" + imageName, "/Description:" + description,
                "/Compress:max", "/NoRestart"
            }, onLine, token);
        }
    }

    /// <summary>原子操作：把源 WIM 的某个版本追加导出到目标 WIM（保留其它版本）。</summary>
    public static void Export(WimEngine engine, string sourceWim, int index, string destWim,
        string imageName, string description, Action<string>? onLine, CancellationToken token)
    {
        if (engine.Kind == WimEngineKind.Wimlib)
        {
            Run(engine, new[]
            {
                "export", sourceWim, index.ToString(), destWim,
                imageName, description, "--compress=LZX:50"
            }, onLine, token);
        }
        else
        {
            Run(engine, new[]
            {
                "/Export-Image", "/SourceImageFile:" + sourceWim, "/SourceIndex:" + index,
                "/DestinationImageFile:" + destWim, "/DestinationName:" + imageName,
                "/Compress:max", "/NoRestart"
            }, onLine, token);
        }
    }

    /// <summary>
    /// wimlib 的 capture 会从离线注册表自动填 <c>DISPLAYNAME</c>（Win11 22H2 仍是
    /// "Windows 10 Home"），把检查器里选中的版本名盖掉。这里把 NAME / DISPLAYNAME /
    /// DISPLAYDESCRIPTION 显式写回，保证输出 ISO 的版本列表与用户选择一致。
    /// DISM 没有等价的轻量属性改写命令，保留其自身元数据。
    /// </summary>
    public static void SetImageProperties(WimEngine engine, string wimPath, int index,
        string name, Action<string>? onLine, CancellationToken token)
    {
        if (engine.Kind != WimEngineKind.Wimlib) return;
        Run(engine, new[]
        {
            "info", wimPath, index.ToString(),
            "--image-property", "NAME=" + name,
            "--image-property", "DISPLAYNAME=" + name,
            "--image-property", "DISPLAYDESCRIPTION=" + name
        }, onLine, token);
    }

    /// <summary>
    /// 启动外部进程并实时抽干 stdout / stderr。取消时 Kill 整棵进程树。
    /// wimlib 的进度用 \r 而不是 \n 换行，因此按字符流读、遇到 \r 或 \n 都切一行。
    /// </summary>
    public static void Run(WimEngine engine, IEnumerable<string> arguments,
        Action<string>? onLine, CancellationToken token)
    {
        if (!engine.Available)
            throw new InvalidOperationException("未找到 wimlib-imagex.exe 或 DISM，无法处理 WIM 映像。");

        token.ThrowIfCancellationRequested();

        var psi = new ProcessStartInfo(engine.ExePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = AppContext.BaseDirectory
        };
        foreach (string a in arguments) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"无法启动 {Path.GetFileName(engine.ExePath)}。");

        var log = new StringBuilder();
        void Emit(string text)
        {
            string line = text.Trim();
            if (line.Length == 0) return;
            lock (log)
            {
                log.AppendLine(line);
                if (log.Length > 64 * 1024) log.Remove(0, log.Length - 64 * 1024);
            }
            onLine?.Invoke(line);
        }

        using var reg = token.Register(() =>
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* 已退出 */ }
        });

        int exitCode = -1;
        try
        {
            Task outTask = PumpAsync(proc.StandardOutput, Emit);
            Task errTask = PumpAsync(proc.StandardError, Emit);
            Task.WaitAll(new[] { outTask, errTask }, Timeout.Infinite, token);
            proc.WaitForExit();
            exitCode = proc.ExitCode;
        }
        finally
        {
            // 取消路径下 WaitAll 可能提前抛出，仍要等进程真正退出再释放句柄
            try { proc.WaitForExit(5000); } catch { /* 已退出 */ }
        }

        if (token.IsCancellationRequested)
            throw new OperationCanceledException(token);

        if (exitCode != 0)
        {
            string tail;
            lock (log) tail = log.ToString();
            string last = string.Join(" | ", tail.Split('\n')
                .Where(l => l.Trim().Length > 0).TakeLast(4)).Trim();
            throw new InvalidOperationException(
                $"{Path.GetFileName(engine.ExePath)} 返回 {exitCode}" +
                (last.Length > 0 ? $"：{last}" : ""));
        }
    }

    private static async Task PumpAsync(StreamReader reader, Action<string> emit)
    {
        var chars = new char[2048];
        var buffer = new StringBuilder();
        try
        {
            while (true)
            {
                int n = await reader.ReadAsync(chars, 0, chars.Length).ConfigureAwait(false);
                if (n <= 0) break;
                for (int i = 0; i < n; i++)
                {
                    char c = chars[i];
                    if (c == '\n' || c == '\r')
                    {
                        if (buffer.Length > 0) { emit(buffer.ToString()); buffer.Clear(); }
                    }
                    else buffer.Append(c);
                }
            }
        }
        catch (ObjectDisposedException) { /* 进程被 Kill 后管道关闭 */ }
        catch (IOException) { /* 同上 */ }
        if (buffer.Length > 0) emit(buffer.ToString());
    }
}
