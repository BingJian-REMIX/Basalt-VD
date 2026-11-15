using System.IO;
using System.Text.RegularExpressions;

namespace Basalt.Services;

/// <summary>清理手术的统计结果。</summary>
public sealed class ImageTrimResult
{
    public CleanupResult Cleanup { get; } = new();
    public List<string> Removed { get; } = new();
    public int DriverGroups { get; set; }
    public int DriverDuplicates { get; set; }
    public int LegacyDriverPackages { get; set; }

    public string Summary =>
        $"移除 {Removed.Count} 个目标 / {Cleanup.DeletedFiles} 个文件，释放 " +
        $"{Models.FileItemFormatter.FormatSize(Cleanup.FreedBytes)}" +
        (DriverGroups > 0 ? $"；检查 {DriverGroups} 组驱动包，去重 {DriverDuplicates} 个旧版本" : "") +
        (LegacyDriverPackages > 0 ? $"，清理 {LegacyDriverPackages} 个遗留驱动包" : "");
}

/// <summary>清理计划：目录交给 CleanupService，散落文件逐个删（同样逐项捕获异常）。</summary>
public sealed class ImageTrimPlan
{
    public List<CleanupTarget> Directories { get; } = new();
    public List<string> Files { get; } = new();
    public int DriverGroups { get; set; }
    public int DriverDuplicates { get; set; }
    public int LegacyDriverPackages { get; set; }

    public IEnumerable<string> Display =>
        Directories.Select(d => d.Display).Concat(Files.Select(f => "[文件] " + f));
}

/// <summary>
/// 对已 apply 到本地的 Windows 映像做「清理手术」。
/// <para>
/// 目录删除直接复用 <see cref="CleanupService.DeleteTargets"/>（逐个捕获权限异常、
/// 跳过 reparse point、防路径越界），这里只负责按精简选项收集目标；
/// 与深度清理的差别是传入 <c>removeTargetRoot: true</c>，连空目录壳一起删掉。
/// 散落在 System32 下的 <c>SecurityHealth*.dll/exe</c> 是文件而非目录，
/// 由本服务按同样的逐项容错方式单独删除。
/// </para>
/// <para>
/// 规则已用真实 Win11 22H2 映像的 <c>wimlib dir</c> 元数据核对：
/// System32 下确有 <c>SecurityHealth</c> 目录 + 9 个同名文件，
/// Edge 实际占 Edge / EdgeCore / EdgeUpdate / EdgeWebView 四个目录，
/// DriverStore 有 688 个独立驱动包且无同 INF 旧版重复 —— 因此驱动项改为
/// 「同 INF 去重 + 清理 prn*/mdm* 遗留打印机/调制解调器驱动包」的组合，既安全又确实生效。
/// </para>
/// </summary>
public static class ImageTrimService
{
    /// <summary>Defender 相关的固定目录（相对 apply 后的映像根）。</summary>
    private static readonly string[] DefenderPaths =
    {
        @"Windows\System32\SecurityHealth",
        @"Windows\SysWOW64\SecurityHealth",
        @"Program Files\Windows Defender",
        @"Program Files (x86)\Windows Defender",
        @"ProgramData\Microsoft\Windows Defender",
        @"Windows\System32\WindowsDefender"
    };

    /// <summary>System32 / SysWOW64 下需要按前缀删除的散落文件。</summary>
    private static readonly string[] DefenderFileDirs = { @"Windows\System32", @"Windows\SysWOW64" };

    /// <summary>Edge / Xbox 等冗余 UWP 应用：按目录名前缀匹配 Windows\SystemApps。</summary>
    private static readonly string[] UwpPrefixes =
    {
        "Microsoft.MicrosoftEdge", "MicrosoftEdge",
        "Microsoft.Xbox", "Microsoft.GamingApp", "Microsoft.GamingServices",
        "Microsoft.Zune", "Microsoft.MicrosoftSolitaireCollection",
        "Microsoft.WindowsFeedbackHub", "Microsoft.YourPhone", "Microsoft.SkypeApp",
        "Microsoft.BingWeather", "Microsoft.GetHelp", "Microsoft.People",
        "Microsoft.Windows.Cortana", "Microsoft.MixedReality.Portal"
    };

    /// <summary>Edge 桌面版实际占用的目录（真实 Win11 22H2 映像核对过）。</summary>
    private static readonly string[] EdgePaths =
    {
        @"Program Files (x86)\Microsoft\Edge",
        @"Program Files\Microsoft\Edge",
        @"Program Files (x86)\Microsoft\EdgeCore",
        @"Program Files\Microsoft\EdgeCore",
        @"Program Files (x86)\Microsoft\EdgeUpdate",
        @"Program Files\Microsoft\EdgeUpdate",
        @"Program Files (x86)\Microsoft\EdgeWebView",
        @"Program Files\Microsoft\EdgeWebView"
    };

    /// <summary>可安全移除的遗留驱动包前缀：打印机 / 调制解调器（不影响启动与安装）。</summary>
    private static readonly string[] LegacyDriverPrefixes = { "prn", "mdm" };

    private static readonly Regex DriverDirRegex = new(
        @"^(?<inf>.+?)\.inf_(?<arch>x86|amd64|arm64)_(?<hash>[0-9a-fA-F]+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>给 UI 预检用：列出会被删除的目录与文件（不实际删除）。</summary>
    public static List<string> Preview(string imageRoot, bool defender, bool edge, bool drivers,
        out int driverGroups, out int legacyPackages)
    {
        var plan = Collect(imageRoot, defender, edge, drivers);
        driverGroups = plan.DriverGroups;
        legacyPackages = plan.LegacyDriverPackages;
        return plan.Display.ToList();
    }

    public static ImageTrimResult Trim(string imageRoot, bool defender, bool edge, bool drivers,
        IProgress<TrimProgress>? progress = null, CancellationToken token = default)
    {
        var result = new ImageTrimResult();
        var plan = Collect(imageRoot, defender, edge, drivers);
        result.DriverGroups = plan.DriverGroups;
        result.DriverDuplicates = plan.DriverDuplicates;
        result.LegacyDriverPackages = plan.LegacyDriverPackages;

        int total = plan.Directories.Count + plan.Files.Count;
        progress?.Report(new TrimProgress
        {
            Stage = TrimStage.Cleaning,
            Message = $"共 {total} 个待清理目标",
            Detail = "目录复用 CleanupService；散落文件逐项容错删除",
            Percent = 0
        });

        // 目录分批交给既有清理逻辑，便于中途取消与阶段内进度上报
        const int BatchSize = 12;
        int done = 0;
        for (int start = 0; start < plan.Directories.Count; start += BatchSize)
        {
            token.ThrowIfCancellationRequested();
            var batch = plan.Directories.Skip(start).Take(BatchSize).ToList();
            var r = CleanupService.DeleteTargets(batch,
                removeTargetRoot: true, clearReadOnly: true);
            // 真实 Windows 映像里系统文件由 TrustedInstaller 所有，光清 ReadOnly 删不动。
            // 对仍然存在的目标夺权后重删；这些目标本就不进新 WIM，保真度不受影响。
            var stubborn = batch.Where(t => Directory.Exists(t.Path)).ToList();
            if (stubborn.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                WindowsAclTool.EnsureDeletable(stubborn.Select(t => t.Path));
                var retried = CleanupService.DeleteTargets(stubborn,
                    removeTargetRoot: true, clearReadOnly: true);
                // 重试删掉的正是首轮被跳过的文件，回滚统计避免假失败
                r.SkippedFiles = Math.Max(0, r.SkippedFiles - retried.DeletedFiles);
                r.Errors.Clear();
                Merge(result.Cleanup, r);
                Merge(result.Cleanup, retried);
            }
            else
            {
                Merge(result.Cleanup, r);
            }
            foreach (var t in batch) result.Removed.Add(t.Display);
            done += batch.Count;
            ReportProgress(progress, batch[^1].Display, done, total, result.Cleanup.FreedBytes);
        }

        // 散落文件：同样逐个捕获权限 / 占用异常，计入同一份统计
        foreach (string relative in plan.Files)
        {
            token.ThrowIfCancellationRequested();
            string full = Path.GetFullPath(Path.Combine(imageRoot, relative));
            try
            {
                DeleteOne(full, result, relative);
            }
            catch (Exception)
            {
                // 同样可能是 TrustedInstaller ACL：夺权后只重试一次
                try
                {
                    WindowsAclTool.EnsureDeletable(full);
                    DeleteOne(full, result, relative);
                }
                catch (Exception ex)
                {
                    result.Cleanup.SkippedFiles++;
                    if (result.Cleanup.Errors.Count < 20)
                        result.Cleanup.Errors.Add($"{relative} — {ex.GetType().Name}: {ex.Message}");
                }
            }
            done++;
            ReportProgress(progress, relative, done, total, result.Cleanup.FreedBytes);
        }

        return result;
    }

    /// <summary>删除单个散落文件：清只读属性后删除，成功后计入统计。</summary>
    private static void DeleteOne(string full, ImageTrimResult result, string relative)
    {
        long size = new FileInfo(full).Length;
        var attr = File.GetAttributes(full);
        if ((attr & FileAttributes.ReadOnly) != 0)
            File.SetAttributes(full, attr & ~FileAttributes.ReadOnly);
        File.Delete(full);
        result.Cleanup.DeletedFiles++;
        result.Cleanup.FreedBytes += size;
        result.Removed.Add(relative);
    }

    private static void ReportProgress(IProgress<TrimProgress>? progress, string current,
        int done, int total, long freed)
    {
        progress?.Report(new TrimProgress
        {
            Stage = TrimStage.Cleaning,
            Message = current,
            Detail = $"{done} / {total} 个目标 · 已释放 " +
                     Models.FileItemFormatter.FormatSize(freed),
            Percent = total == 0 ? 100 : done * 100 / total
        });
    }

    private static ImageTrimPlan Collect(string imageRoot, bool defender, bool edge, bool drivers)
    {
        string root = Path.GetFullPath(imageRoot);
        var plan = new ImageTrimPlan();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        bool Inside(string full) => full.StartsWith(root.TrimEnd('\\') + '\\',
            StringComparison.OrdinalIgnoreCase);

        void AddDir(string relative)
        {
            string full = Path.GetFullPath(Path.Combine(root, relative));
            if (!Inside(full) || !Directory.Exists(full) || !seen.Add(full)) return;
            plan.Directories.Add(new CleanupTarget
            {
                Display = relative,
                Path = full,
                RootPath = root,
                IsSelected = true
            });
        }

        void AddFile(string relative)
        {
            string full = Path.GetFullPath(Path.Combine(root, relative));
            if (!Inside(full) || !File.Exists(full) || !seen.Add(full)) return;
            plan.Files.Add(relative);
        }

        void AddUnder(string parentRelative, Func<string, bool> match)
        {
            string parent = Path.Combine(root, parentRelative);
            if (!Directory.Exists(parent)) return;
            string[] dirs;
            try { dirs = Directory.GetDirectories(parent); }
            catch { return; }
            foreach (string dir in dirs)
                if (match(Path.GetFileName(dir))) AddDir(Path.GetRelativePath(root, dir));
        }

        if (defender)
        {
            foreach (string p in DefenderPaths) AddDir(p);
            // WinSxS 里的 Defender 组件目录带通配符
            string winsxs = Path.Combine(root, "Windows", "WinSxS");
            if (Directory.Exists(winsxs))
            {
                try
                {
                    foreach (string dir in Directory.GetDirectories(winsxs, "amd64_windows-defender_*"))
                        AddDir(Path.GetRelativePath(root, dir));
                }
                catch { /* 无权限时跳过 */ }
            }

            foreach (string dir in DefenderFileDirs)
            {
                string full = Path.Combine(root, dir);
                if (!Directory.Exists(full)) continue;
                string[] files;
                try { files = Directory.GetFiles(full, "SecurityHealth*.*"); }
                catch { continue; }
                foreach (string file in files)
                    AddFile(Path.GetRelativePath(root, file));
            }

            AddUnder(@"Windows\SystemApps", n => n.StartsWith("Microsoft.Windows.SecHealthUI",
                StringComparison.OrdinalIgnoreCase));
        }

        if (edge)
        {
            foreach (string p in EdgePaths) AddDir(p);
            AddUnder(@"Windows\SystemApps", n => UwpPrefixes.Any(prefix =>
                n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));
            AddUnder(@"Program Files\WindowsApps", n => UwpPrefixes.Any(prefix =>
                n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));
        }

        if (drivers)
        {
            string repo = Path.Combine(root, @"Windows\System32\DriverStore\FileRepository");
            if (Directory.Exists(repo))
            {
                var groups = new Dictionary<string, List<(string Path, DateTime Time)>>(
                    StringComparer.OrdinalIgnoreCase);
                string[] dirs;
                try { dirs = Directory.GetDirectories(repo); } catch { dirs = Array.Empty<string>(); }

                foreach (string dir in dirs)
                {
                    string name = Path.GetFileName(dir);
                    var m = DriverDirRegex.Match(name);
                    if (!m.Success) continue;

                    // 1) 遗留打印机 / 调制解调器驱动包：明确可移除，不影响启动与安装
                    if (LegacyDriverPrefixes.Any(p => name.StartsWith(p,
                            StringComparison.OrdinalIgnoreCase)))
                    {
                        AddDir(Path.GetRelativePath(root, dir));
                        plan.LegacyDriverPackages++;
                        continue;
                    }

                    // 2) 同一 INF + 体系结构的多个版本：只保留时间戳最新的一份
                    string key = m.Groups["inf"].Value + "|" + m.Groups["arch"].Value.ToLowerInvariant();
                    if (!groups.TryGetValue(key, out var list)) groups[key] = list = new();
                    DateTime time;
                    try { time = Directory.GetLastWriteTimeUtc(dir); } catch { time = DateTime.MinValue; }
                    list.Add((dir, time));
                }

                plan.DriverGroups = groups.Count;
                foreach (var g in groups)
                {
                    if (g.Value.Count < 2) continue;
                    foreach (var old in g.Value.OrderByDescending(x => x.Time).Skip(1))
                    {
                        AddDir(Path.GetRelativePath(root, old.Path));
                        plan.DriverDuplicates++;
                    }
                }
            }
        }

        return plan;
    }

    private static void Merge(CleanupResult into, CleanupResult from)
    {
        into.DeletedFiles += from.DeletedFiles;
        into.SkippedFiles += from.SkippedFiles;
        into.FreedBytes += from.FreedBytes;
        foreach (string e in from.Errors)
        {
            if (into.Errors.Count >= 20) break;
            into.Errors.Add(e);
        }
    }
}
