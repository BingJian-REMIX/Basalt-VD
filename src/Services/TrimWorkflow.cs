using System.Diagnostics;
using System.IO;

namespace Basalt.Services;

/// <summary>
/// 「一键精简系统镜像」的完整编排：
/// 准备 → 提取（原 WIM 出 ISO）→ 释放（apply）→ 清理（CleanupService）→
/// 封装（capture / export）→ 替换（ISO 原位写回）。
/// <para>
/// 全程不修改源 ISO 与源 install.wim：先把原始 WIM 按物理 extent 复制到工作目录，
/// 所有 apply / 删除 / capture 都只作用于工作目录里的副本，最后写出一个全新的 ISO。
/// </para>
/// </summary>
public static class TrimWorkflow
{
    private const long ReserveBytes = 512L << 20;

    /// <summary>默认工作根目录（用户可在界面里改到空间更大的磁盘）。</summary>
    public static string DefaultWorkRoot() => Path.Combine(Path.GetTempPath(), "Basalt_Wim_Work");

    /// <summary>给出大致的空间需求，用于开始前的预检与 UI 提示。</summary>
    public static long EstimateRequiredBytes(TrimOptions options, long isoSize)
    {
        long wim = Math.Max(1, options.Inspection.InstallImageSize);
        long expanded = options.Inspection.Images
            .FirstOrDefault(i => i.Index == options.ImageIndex)?.TotalBytes ?? 0;
        if (expanded <= 0) expanded = wim * 2;      // XML 没有 TOTALBYTES 时的保守估计

        // 源 WIM 副本 + apply 展开的目录树 + 新 WIM + 输出 ISO（稀疏写入按 1/4 估计）
        return wim + expanded + wim + isoSize / 4 + ReserveBytes;
    }

    /// <summary>选择工作根目录：优先用户指定，其次 %TEMP%，再自动挑剩余空间最大的固定盘。</summary>
    public static string ResolveWorkRoot(string configured, long needed)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(configured))
        {
            candidates.Add(configured);
        }
        else
        {
            candidates.Add(DefaultWorkRoot());
            foreach (var d in DriveInfo.GetDrives()
                         .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
                         .OrderByDescending(d => d.AvailableFreeSpace))
                candidates.Add(Path.Combine(d.RootDirectory.FullName, "Basalt_Wim_Work"));
        }

        string best = "";
        long bestFree = 0;
        foreach (string c in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                string root = Path.GetPathRoot(Path.GetFullPath(c)) ?? "";
                if (root.Length == 0) continue;
                var drive = new DriveInfo(root);
                if (!drive.IsReady) continue;
                if (drive.AvailableFreeSpace > bestFree)
                {
                    best = c;
                    bestFree = drive.AvailableFreeSpace;
                }
            }
            catch { /* 不可访问的盘跳过 */ }
        }

        if (best.Length == 0)
            throw new InvalidOperationException("找不到可用的工作磁盘。");

        if (bestFree < needed)
            throw new InvalidOperationException(
                $"可用空间不足：本次精简预计需要约 {Models.FileItemFormatter.FormatSize(needed)}，" +
                $"而 {best} 所在磁盘只剩 {Models.FileItemFormatter.FormatSize(bestFree)}。" +
                "请在检查器底部的「工作目录」里指定一个空间更大的磁盘后再试。");

        Directory.CreateDirectory(best);
        return best;
    }

    /// <summary>输出 ISO 绝不覆盖已有文件。</summary>
    public static string ResolveOutputPath(string configured, string sourceIso)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);
        string dir = Path.GetDirectoryName(Path.GetFullPath(sourceIso)) ?? Path.GetTempPath();
        string stem = Path.GetFileNameWithoutExtension(sourceIso) + "-basalt-trimmed";
        string candidate = Path.Combine(dir, stem + ".iso");
        for (int i = 2; File.Exists(candidate); i++)
            candidate = Path.Combine(dir, $"{stem}-{i}.iso");
        return candidate;
    }

    public static TrimResult Run(TrimOptions options,
        IProgress<TrimProgress>? progress = null, CancellationToken token = default)
    {
        var result = new TrimResult();
        var reporter = new Reporter(progress);
        var sw = Stopwatch.StartNew();
        string jobRoot = "";
        string output = "";
        bool outputCreated = false;

        try
        {
            token.ThrowIfCancellationRequested();

            // ---------- 准备 ----------
            reporter.Send(TrimStage.Preparing, "正在校验输入与工具链", force: true);
            string iso = Path.GetFullPath(options.SourceIsoPath);
            if (!File.Exists(iso)) throw new FileNotFoundException("源 ISO 不存在。", iso);
            var inspection = options.Inspection;
            if (inspection.InstallImagePath == null || inspection.InstallImageSize <= 0)
                throw new InvalidOperationException("没有可用的 install.wim / install.esd，无法精简。");
            if (!options.HasAnyOption)
                throw new InvalidOperationException("未勾选任何精简项。");

            var engine = WimTool.Resolve();
            if (!engine.Available)
                throw new InvalidOperationException(
                    "未找到 wimlib-imagex.exe（tools\\wimlib-x64）或系统 DISM，无法处理 WIM。");
            result.Notes.Add("引擎：" + engine.Label);

            string entry = inspection.InstallImagePath.Replace('/', '\\').Trim('\\');
            byte[] head = IsoRandomAccess.TryReadRange(iso, entry, 0, WimMetadataReader.HeaderSize)
                          ?? Array.Empty<byte>();
            if (head.Length < WimMetadataReader.HeaderSize)
            {
                using var disk = new ContainerDiskService(iso);
                head = disk.ReadHead(entry, WimMetadataReader.HeaderSize);
            }
            var loc = WimMetadataReader.LocateXml(head);
            if (!loc.Success) throw new InvalidOperationException("无法解析 WIM 头：" + loc.Note);

            result.OriginalIsoSize = new FileInfo(iso).Length;
            result.OriginalWimSize = inspection.InstallImageSize;
            long needed = EstimateRequiredBytes(options, result.OriginalIsoSize);

            reporter.Send(TrimStage.Preparing,
                "正在定位 install.wim 的物理位置",
                $"源 ISO {Models.FileItemFormatter.FormatSize(result.OriginalIsoSize)} · " +
                $"预计需要 {Models.FileItemFormatter.FormatSize(needed)} 临时空间", force: true);

            var extent = IsoExtentService.Find(iso, entry, inspection.InstallImageSize, loc.Offset, token)
                ?? throw new InvalidOperationException(
                    "未能在 ISO 中定位 install.wim 的连续物理 extent（分卷 WIM / 非 UDF 镜像暂不支持）。");
            if (!extent.Verified)
                throw new InvalidOperationException(
                    "install.wim 疑似跨多个非连续 extent，原位替换会破坏镜像，已中止。");
            result.Notes.Add("WIM extent：" + extent.Display + " · " + extent.Note);

            string workRoot = ResolveWorkRoot(options.WorkRoot, needed);
            output = ResolveOutputPath(options.OutputIsoPath, iso);
            if (File.Exists(output))
                throw new InvalidOperationException($"输出文件已存在，拒绝覆盖：{output}");
            result.OutputIsoPath = output;
            result.WorkRoot = Path.Combine(workRoot, "job-" + Guid.NewGuid().ToString("N")[..8]);
            jobRoot = result.WorkRoot;
            Directory.CreateDirectory(jobRoot);

            // ---------- 提取：把原始 WIM 从 ISO 的 extent 复制出来 ----------
            string sourceWim = Path.Combine(jobRoot, "install.source.wim");
            reporter.Send(TrimStage.Extracting, "正在提取原始 install.wim",
                "按物理偏移复制，不解压 ISO、不改动源文件", 0, force: true);
            IsoExtentService.ExtractRaw(iso, extent.Offset, extent.Length, sourceWim,
                (done, total) => reporter.Send(TrimStage.Extracting, "正在提取原始 install.wim",
                    $"{Models.FileItemFormatter.FormatSize(done)} / {Models.FileItemFormatter.FormatSize(total)}",
                    total == 0 ? null : (int)(done * 100 / total), done, total), token);
            reporter.Send(TrimStage.Extracting, "原始映像已提取",
                Models.FileItemFormatter.FormatSize(extent.Length), 100, force: true);

            // ---------- 释放：apply 到工作目录 ----------
            string imageRoot = Path.Combine(jobRoot, "image");
            reporter.Send(TrimStage.Applying, $"正在释放 #{options.ImageIndex} {options.ImageName}",
                engine.Label, null, force: true);
            WimTool.Apply(engine, sourceWim, options.ImageIndex, imageRoot,
                line => reporter.Send(TrimStage.Applying,
                    $"正在释放 #{options.ImageIndex} {options.ImageName}",
                    Shorten(line), WimTool.ParsePercent(line)), token);
            reporter.Send(TrimStage.Applying, "映像已释放到工作目录", imageRoot, 100, force: true);

            // ---------- 清理手术 ----------
            var trim = ImageTrimService.Trim(imageRoot, options.RemoveDefender,
                options.RemoveEdge, options.RemoveDrivers, progress, token);
            result.Cleanup = trim.Cleanup;
            result.Notes.Add(trim.Summary);
            reporter.Send(TrimStage.Cleaning, "清理手术完成", trim.Summary, 100, force: true);

            // ---------- 封装 ----------
            string newWim = Path.Combine(jobRoot, "install.trimmed.wim");
            reporter.Send(TrimStage.Capturing, "正在重新封装 install.wim",
                engine.Label, null, force: true);
            WimTool.Capture(engine, imageRoot, newWim, options.ImageName,
                options.ImageDescription,
                line => reporter.Send(TrimStage.Capturing, "正在重新封装 install.wim",
                    Shorten(line), WimTool.ParsePercent(line)), token);

            int kept = 0;
            if (options.KeepOtherImages)
            {
                foreach (var img in inspection.Images
                             .Where(i => i.Index != options.ImageIndex)
                             .OrderBy(i => i.Index))
                {
                    token.ThrowIfCancellationRequested();
                    reporter.Send(TrimStage.Capturing, $"保留原版本 #{img.Index} {img.Name}",
                        "export 未精简的映像到新 WIM", null, force: true);
                    WimTool.Export(engine, sourceWim, img.Index, newWim, img.Name,
                        img.Description,
                        line => reporter.Send(TrimStage.Capturing,
                            $"保留原版本 #{img.Index} {img.Name}",
                            Shorten(line), WimTool.ParsePercent(line)), token);
                    kept++;
                }
                if (kept > 0)
                    result.Notes.Add($"已 export 保留其余 {kept} 个版本；精简后的版本排到索引 #1");
            }
            else
            {
                result.Notes.Add("未勾选保留其它版本：新 install.wim 仅含精简后的 1 个映像");
            }

            // 放在 export 之后：wimlib 每次追加映像都会重建 XML 元数据，提前回写会被覆盖。
            // capture 本身会用离线注册表的 PRODUCTNAME 自动填 DISPLAYNAME
            // （Win11 22H2 镜像里仍是 "Windows 10 Home"），必须显式回写成用户选中的版本名。
            WimTool.SetImageProperties(engine, newWim, 1, options.ImageName,
                line => reporter.Send(TrimStage.Capturing, "正在回写版本元数据",
                    Shorten(line), null), token);
            if (engine.Kind == WimEngineKind.Wimlib)
                result.Notes.Add($"版本名已回写为「{options.ImageName}」");

            long newSize = new FileInfo(newWim).Length;
            if (newSize > extent.Length)
                throw new InvalidOperationException(
                    $"新 WIM（{Models.FileItemFormatter.FormatSize(newSize)}）大于原 extent " +
                    $"{Models.FileItemFormatter.FormatSize(extent.Length)}，无法原位替换。" +
                    "可减少保留的版本或换用更高压缩级别。");
            reporter.Send(TrimStage.Capturing, "封装完成",
                $"{Models.FileItemFormatter.FormatSize(extent.Length)} → " +
                Models.FileItemFormatter.FormatSize(newSize), 100, force: true);

            // 先删掉最占空间的两块，为输出 ISO 腾地方（源 WIM 只在 export 前需要）
            reporter.Send(TrimStage.Replacing, "正在回收临时空间", force: true);
            TryDeleteDirectory(imageRoot);
            TryDeleteFile(sourceWim);

            // ---------- 替换：原位写回 ISO 副本 ----------
            reporter.Send(TrimStage.Replacing, "正在写入精简版 ISO",
                "保留 UDF 目录项与引导结构；缩短部分写成 NTFS 稀疏空洞", 0, force: true);
            var patch = IsoExtentService.Replace(iso, entry, extent, newWim, output,
                (done, total) => reporter.Send(TrimStage.Replacing, "正在写入精简版 ISO",
                    $"{Models.FileItemFormatter.FormatSize(done)} / {Models.FileItemFormatter.FormatSize(total)}",
                    total == 0 ? null : (int)(done * 100 / total), done, total), token);
            outputCreated = true;

            result.NewWimSize = patch.NewWimSize;
            result.OutputLogicalSize = patch.OutputLogicalSize;
            result.Notes.Add(patch.Verification);
            result.Notes.Add(patch.Sparse
                ? $"稀疏写入生效：逻辑尺寸不变，实际少占 {Models.FileItemFormatter.FormatSize(patch.PhysicalSavedBytes)}"
                : "当前文件系统不支持稀疏写入：逻辑尺寸与实际占用均不变（精简内容仍然生效）");
            result.Notes.Add("源 ISO 全程只读，未做任何写入");

            result.Success = true;
            reporter.Send(TrimStage.Done, "精简版 ISO 已生成",
                $"{output} · {result.SizeText}", 100, force: true);
        }
        catch (OperationCanceledException)
        {
            result.Cancelled = true;
            result.Error = "用户取消了操作。";
            reporter.Send(TrimStage.Cancelled, "已取消", "正在清理临时文件", 0, force: true);
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
            reporter.Send(TrimStage.Failed, "精简失败", Shorten(ex.Message, 200), 0, force: true);
        }
        finally
        {
            if (outputCreated && !result.Success)
                TryDeleteFile(output);

            if (jobRoot.Length > 0 && !options.KeepWorkDir)
                TryDeleteDirectory(jobRoot);
            else if (jobRoot.Length > 0)
                result.Notes.Add("已按调试设置保留工作目录：" + jobRoot);

            result.StageLog.AddRange(reporter.Log);
            result.StageLog.Add($"总耗时 {sw.ElapsedMilliseconds} ms");
        }

        return result;
    }

    private static string Shorten(string s, int max = 110)
        => s.Length <= max ? s : s[..max] + "…";

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* 清理失败不影响主结果 */ }
    }

    /// <summary>
    /// 删除 apply 出来的大树。wimlib 会把映像里的系统文件还原成 ReadOnly，
    /// 不开启 clearReadOnly 会留下整棵目录树（真实 Win11 实跑实测残留 6.6 GB）。
    /// 更麻烦的是这些文件还保留了 TrustedInstaller owner / ACL，标准令牌只有
    /// ReadAndExecute，连管理员也删不掉；因此残留时再用 takeown + icacls 夺权。
    /// 只在临时副本上动 ACL，capture 已经完成，不会改变输出 WIM 的安全描述符。
    /// </summary>
    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return;
            string parent = Path.GetDirectoryName(Path.GetFullPath(path)) ?? "";
            CleanupTarget Target() => new()
            {
                Display = Path.GetFileName(path), Path = path, RootPath = parent
            };

            CleanupService.DeleteTargets(new[] { Target() },
                removeTargetRoot: true, clearReadOnly: true);
            if (Directory.Exists(path))
            {
                try { Directory.Delete(path, recursive: true); } catch { }
            }

            if (!Directory.Exists(path)) return;
            // 顽固 ACL：夺取所有权 → 给 Administrators 完全控制 → 再删一轮
            WindowsAclTool.EnsureDeletable(path);
            CleanupService.DeleteTargets(new[] { Target() },
                removeTargetRoot: true, clearReadOnly: true);
            if (Directory.Exists(path))
            {
                try { Directory.Delete(path, recursive: true); } catch { }
            }
        }
        catch { /* 清理失败不影响主结果 */ }
    }

    /// <summary>维护入口：清理历史精简任务残留的工作目录，返回可写入报告的说明。</summary>
    public static string CleanupWorkDir(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "未指定目录。";
        if (!Directory.Exists(path)) return "目录不存在或已清理：" + path;
        TryDeleteDirectory(path);
        return Directory.Exists(path)
            ? "清理未完成（可能仍有链接 / 占用）：" + path
            : "已清理：" + path;
    }

    /// <summary>进度节流：阶段切换 / 强制上报立即发，否则每 120 ms 最多发一次。</summary>
    private sealed class Reporter
    {
        private readonly IProgress<TrimProgress>? _progress;
        private readonly Stopwatch _sw = Stopwatch.StartNew();
        private long _last;
        private TrimStage _lastStage = (TrimStage)(-1);

        public List<string> Log { get; } = new();

        public Reporter(IProgress<TrimProgress>? progress) => _progress = progress;

        public void Send(TrimStage stage, string message, string detail = "",
            int? percent = null, long done = 0, long total = 0, bool force = false)
        {
            bool changed = stage != _lastStage;
            if (changed || force) Log.Add($"[{TrimStageNames.Of(stage)}] {message}");
            if (!force && !changed && _sw.ElapsedMilliseconds - _last < 120) return;

            _last = _sw.ElapsedMilliseconds;
            _lastStage = stage;
            _progress?.Report(new TrimProgress
            {
                Stage = stage,
                Message = message,
                Detail = detail,
                Percent = percent,
                BytesDone = done,
                BytesTotal = total
            });
        }
    }
}
