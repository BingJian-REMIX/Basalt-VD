using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DiscUtils.Iso9660;

namespace Basalt.Services;

/// <summary>
/// 「一键精简系统镜像」端到端自测。
/// <para>
/// 夹具全部真实：用随程序分发的 wimlib 把一棵模拟 Windows 目录树 capture 成真正的 WIM，
/// 再用 DiscUtils.CDBuilder 打成真正的 ISO9660 镜像，然后完整跑一遍
/// TrimWorkflow（提取 → apply → CleanupService 清理 → capture → ISO 原位替换）。
/// 不依赖真实 Windows ISO、不需要管理员权限、不需要几十 GB 磁盘。
/// </para>
/// </summary>
public static class TrimSelfTest
{
    private static void W(StringBuilder sb, string s) => sb.AppendLine(s);

    // ==================== --trimtest ====================

    public static string Run()
    {
        var sb = new StringBuilder();
        int pass = 0, fail = 0;
        void Check(bool ok, string label, string detail = "")
        {
            W(sb, $"   [{(ok ? "PASS" : "FAIL")}] {label}{(detail.Length > 0 ? " — " + detail : "")}");
            if (ok) pass++; else fail++;
        }

        W(sb, "=== Basalt 一键精简 端到端自测 ===");
        W(sb, "");

        var engine = WimTool.Resolve();
        W(sb, $"WIM 引擎: {engine.Label} ({engine.ExePath})");
        Check(engine.Available, "WIM 引擎可用", engine.Label);
        if (!engine.Available) return Finish();

        string stage = Path.Combine(TestStageRoot(), $"BasaltTrimTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(stage);
        try
        {
            // ---------- 1. 造一棵模拟 Windows 映像目录树 ----------
            W(sb, "-- 1. 构造模拟 Windows 映像 --");
            string tree = Path.Combine(stage, "tree");
            BuildFakeWindowsTree(tree);
            Check(File.Exists(Path.Combine(tree, "Windows", "System32", "notepad.exe")),
                "基准文件存在（不该被删）");
            Check(Directory.Exists(Path.Combine(tree, "Windows", "System32", "SecurityHealth")),
                "Defender 目录存在");
            Check(Directory.Exists(Path.Combine(tree, "Program Files (x86)", "Microsoft", "Edge")),
                "Edge 目录存在");

            var preview = ImageTrimService.Preview(tree, true, true, true,
                out int driverGroups, out int legacyPackages);
            Check(preview.Count >= 8, "Preview 列出待删目录与文件且不实际删除", preview.Count + " 项");
            Check(driverGroups > 0 && legacyPackages >= 2,
                "驱动规则同时覆盖去重分组与遗留驱动包",
                $"{driverGroups} 组 / {legacyPackages} 个遗留包");
            Check(preview.Any(p => p.StartsWith("[文件]")), "Preview 覆盖散落的 SecurityHealth 文件");
            Check(Directory.Exists(Path.Combine(tree, "Windows", "System32", "SecurityHealth")),
                "Preview 未修改目录树");

            // ---------- 2. wimlib capture → 真 WIM ----------
            W(sb, "");
            W(sb, "-- 2. wimlib capture 生成真实 install.wim --");
            string sourceWim = Path.Combine(stage, "install.wim");
            WimTool.Capture(engine, tree, sourceWim, "Windows 11 Pro (Test)", "Basalt trim fixture",
                null, CancellationToken.None);
            long wimSize = new FileInfo(sourceWim).Length;
            Check(wimSize > 100_000, "capture 产出真实 WIM", wimSize + " 字节");

            byte[] wimHead = ReadAt(sourceWim, 0, WimMetadataReader.HeaderSize);
            var loc = WimMetadataReader.LocateXml(wimHead);
            Check(loc.Success, "wimlib 生成的 WIM 头可被纯托管解析", loc.Note ?? "");
            byte[] xml = ReadAt(sourceWim, loc.Offset, (int)loc.Size);
            var parsed = WimMetadataReader.Read(wimHead, xml, "夹具 WIM");
            Check(parsed.Success && parsed.Images.Count == 1,
                "XML 版本列表可读", parsed.Success ? parsed.Images.Count + " 个映像" : parsed.Note ?? "");

            // ---------- 3. export 命令语义：新建 + 追加 ----------
            W(sb, "");
            W(sb, "-- 3. wimlib export 保留其它版本 --");
            string exported = Path.Combine(stage, "exported.wim");
            WimTool.Export(engine, sourceWim, 1, exported, "Copy One", "d", null, CancellationToken.None);
            byte[] e1 = ReadAt(exported, 0, WimMetadataReader.HeaderSize);
            var l1 = WimMetadataReader.LocateXml(e1);
            var p1 = WimMetadataReader.Read(e1, ReadAt(exported, l1.Offset, (int)l1.Size));
            Check(p1.Success && p1.Images.Count == 1, "export 到新 WIM 成功",
                p1.Success ? p1.Images[0].Name : p1.Note ?? "");

            WimTool.Export(engine, sourceWim, 1, exported, "Copy Two", "d", null, CancellationToken.None);
            byte[] e2 = ReadAt(exported, 0, WimMetadataReader.HeaderSize);
            var l2 = WimMetadataReader.LocateXml(e2);
            var p2 = WimMetadataReader.Read(e2, ReadAt(exported, l2.Offset, (int)l2.Size));
            Check(p2.Success && p2.Images.Count == 2, "再次 export 追加为第 2 个映像",
                p2.Success ? p2.Images.Count + " 个映像" : p2.Note ?? "");

            // ---------- 4. CDBuilder → 真 ISO ----------
            W(sb, "");
            W(sb, "-- 4. 构造真实 ISO 夹具 --");
            string iso = Path.Combine(stage, "fixture.iso");
            BuildIso(iso, sourceWim);
            long isoSize = new FileInfo(iso).Length;
            Check(isoSize > wimSize, "ISO 夹具已生成", isoSize + " 字节");
            Check(ContainerDetector.DetectFile(iso) == ContainerFormat.Iso,
                "容器检测识别为 ISO");

            byte[] isoHashBefore = Sha256(iso);
            DateTime isoTimeBefore = File.GetLastWriteTimeUtc(iso);

            using var disk = new ContainerDiskService(iso);
            var inspection = IsoInspectorService.Inspect(disk);
            Check(inspection.InstallImageName == "install.wim", "检查器定位 install.wim",
                inspection.InstallImageDisplay);
            Check(inspection.InstallImageSize == wimSize, "检查器读取到正确大小",
                $"{inspection.InstallImageSize} / {wimSize}");
            Check(inspection.Images.Count == 1, "检查器解析出 1 个版本",
                inspection.Images.Count + " 项");
            Check(inspection.Error == null, "无降级提示", inspection.Error ?? "");

            string entry = inspection.InstallImagePath!;
            byte[] head = IsoRandomAccess.TryReadRange(iso, entry, 0, WimMetadataReader.HeaderSize)
                          ?? Array.Empty<byte>();
            var headLoc = WimMetadataReader.LocateXml(head);
            Check(headLoc.Success, "ISO 内 WIM 头可读", headLoc.Note ?? "");

            var extent = IsoExtentService.Find(iso, entry, wimSize, headLoc.Offset,
                CancellationToken.None);
            Check(extent != null, "定位到 install.wim 的物理 extent");
            if (extent == null) return Finish();
            Check(extent.Offset % 2048 == 0, "extent 扇区对齐", extent.Display);
            Check(extent.Verified, "UDF/ISO9660 流交叉校验通过", extent.Note);

            // ---------- 5. 完整精简流水线 ----------
            W(sb, "");
            W(sb, "-- 5. TrimWorkflow 完整流水线 --");
            string output = Path.Combine(stage, "trimmed.iso");
            var options = new TrimOptions
            {
                SourceIsoPath = iso,
                Inspection = inspection,
                ImageIndex = inspection.Images[0].Index,
                ImageName = inspection.Images[0].Name,
                ImageDescription = "Basalt 精简版测试",
                RemoveDefender = true,
                RemoveEdge = true,
                RemoveDrivers = true,
                KeepOtherImages = false,
                WorkRoot = Path.Combine(stage, "work"),
                OutputIsoPath = output,
                KeepWorkDir = true
            };

            var seen = new List<TrimProgress>();
            // 无界面模式没有 WPF 消息泵，Progress<T> 的回调会异步投递；这里用同步实现
            IProgress<TrimProgress> progress = new SyncProgress<TrimProgress>(p => seen.Add(p));
            var sw = Stopwatch.StartNew();
            var result = TrimWorkflow.Run(options, progress, CancellationToken.None);
            sw.Stop();
            W(sb, $"耗时: {sw.ElapsedMilliseconds} ms · 阶段数: {seen.Count}");
            foreach (string line in result.StageLog) W(sb, "   " + line);
            if (result.Error != null) W(sb, "   错误: " + result.Error);
            foreach (string note in result.Notes) W(sb, "   备注: " + note);

            Check(result.Success, "精简流水线成功", result.Error ?? "");
            if (!result.Success) return Finish();
            Check(result.Cleanup != null && result.Cleanup.DeletedFiles > 0,
                "清理手术删除了文件", result.Cleanup?.Summary ?? "");
            Check(result.NewWimSize > 0 && result.NewWimSize < result.OriginalWimSize,
                "新 WIM 比原始 WIM 更小", result.SizeText);
            Check(result.OutputIsoPath != null && File.Exists(result.OutputIsoPath),
                "输出 ISO 已生成", result.OutputIsoPath ?? "");
            Check(result.OutputLogicalSize == isoSize,
                "输出 ISO 逻辑尺寸保持不变（UDF 结构完整）",
                $"{result.OutputLogicalSize} / {isoSize}");
            Check(result.SavedBytes > 0, "计算出了实际节省的字节",
                Models.FileItemFormatter.FormatSize(result.SavedBytes));

            string[] stages = { "准备中", "提取中", "释放中", "清理中", "封装中", "替换中", "已完成" };
            foreach (string s in stages)
                Check(result.StageLog.Any(l => l.Contains(s)), "阶段日志包含「" + s + "」");
            Check(seen.Any(p => p.Percent.HasValue), "上报过确定进度百分比");
            Check(seen.Any(p => p.Stage == TrimStage.Done), "上报过完成阶段");

            // 源 ISO 全程只读
            Check(Sha256(iso).SequenceEqual(isoHashBefore), "源 ISO 内容未被修改");
            Check(File.GetLastWriteTimeUtc(iso) == isoTimeBefore, "源 ISO 时间戳未变化");

            // 输出 ISO 复核
            Check(result.Notes.Any(n => n.Contains("版本列表可读")),
                "输出 ISO 内的 WIM 版本列表可读",
                result.Notes.FirstOrDefault(n => n.Contains("版本列表")) ?? "");
            byte[] outHead = IsoRandomAccess.TryReadRange(output, entry, 0,
                WimMetadataReader.HeaderSize) ?? Array.Empty<byte>();
            string newWim = Path.Combine(result.WorkRoot!, "install.trimmed.wim");
            byte[] newHead = ReadAt(newWim, 0, WimMetadataReader.HeaderSize);
            Check(outHead.AsSpan().SequenceEqual(newHead),
                "ISO 内 WIM 头与新 WIM 完全一致");
            Check(Can7ZipList(output), "7-Zip 仍能正常列出精简版 ISO");

            // 把新 WIM apply 回来，验证精简目录真的消失、基准文件还在
            string verifyDir = Path.Combine(stage, "verify");
            WimTool.Apply(engine, newWim, 1, verifyDir, null, CancellationToken.None);
            Check(!Directory.Exists(Path.Combine(verifyDir, "Windows", "System32", "SecurityHealth")),
                "Defender 目录已从映像中移除");
            Check(!File.Exists(Path.Combine(verifyDir, "Windows", "System32", "SecurityHealthAgent.dll")),
                "散落的 SecurityHealth 文件已移除");
            Check(!Directory.Exists(Path.Combine(verifyDir, "Program Files (x86)", "Microsoft", "Edge")),
                "Edge 目录已从映像中移除");
            Check(!Directory.Exists(Path.Combine(verifyDir, "Program Files (x86)", "Microsoft", "EdgeUpdate")) &&
                  !Directory.Exists(Path.Combine(verifyDir, "Program Files (x86)", "Microsoft", "EdgeWebView")),
                "EdgeUpdate / EdgeWebView 已移除");
            Check(!Directory.EnumerateDirectories(
                    Path.Combine(verifyDir, "Windows", "SystemApps"), "Microsoft.Xbox*").Any(),
                "Xbox UWP 目录已从映像中移除");
            Check(File.Exists(Path.Combine(verifyDir, "Windows", "System32", "notepad.exe")),
                "基准文件仍然保留");
            string repo = Path.Combine(verifyDir, "Windows", "System32", "DriverStore", "FileRepository");
            int driverDirs = Directory.Exists(repo) ? Directory.GetDirectories(repo).Length : 0;
            Check(driverDirs == 2, "旧版驱动去重 + 遗留 prn/mdm 包清理生效",
                driverDirs + " 个目录（应为 foo 最新 + bar）");

            // ---------- 6. 取消与防覆盖 ----------
            W(sb, "");
            W(sb, "-- 6. 取消 / 输出防覆盖 --");
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            string cancelOut = Path.Combine(stage, "cancelled.iso");
            var cancelOptions = Clone(options, cancelOut);
            var cancelled = TrimWorkflow.Run(cancelOptions, null, cts.Token);
            Check(cancelled.Cancelled && !cancelled.Success, "预取消令牌安全中止");
            Check(!File.Exists(cancelOut), "取消时没有留下输出 ISO");

            string auto1 = TrimWorkflow.ResolveOutputPath("", iso);
            File.WriteAllText(auto1, "occupied");
            string auto2 = TrimWorkflow.ResolveOutputPath("", iso);
            Check(auto2 != auto1 && !File.Exists(auto2), "输出路径自动避让已有文件", auto2);

            // ---------- 7. 真实实跑暴露的两个质量问题 ----------
            W(sb, "");
            W(sb, "-- 7. 只读系统文件清理 / 版本元数据回写 --");

            // 7a. wimlib apply 出来的系统文件带 ReadOnly，旧逻辑会抛 UnauthorizedAccessException。
            //     用「不开启 / 开启 clearReadOnly」两组对照，确保测试本身有效。
            string roRoot = Path.Combine(stage, "readonly");
            var roFiles = new List<string>();
            foreach (string variant in new[] { "off", "on" })
            {
                string dir = Path.Combine(roRoot, variant, "SecurityHealth");
                Directory.CreateDirectory(dir);
                string f = Path.Combine(dir, "SecurityHealthService.exe");
                File.WriteAllBytes(f, new byte[8192]);
                File.SetAttributes(f, File.GetAttributes(f) | FileAttributes.ReadOnly);
                roFiles.Add(f);
                var target = new CleanupTarget
                {
                    Display = variant, Path = dir, RootPath = roRoot,
                    Bytes = 8192, FileCount = 1
                };
                var r = CleanupService.DeleteTargets(new[] { target },
                    removeTargetRoot: true, clearReadOnly: variant == "on");
                if (variant == "off")
                    Check(r.SkippedFiles > 0 && File.Exists(f),
                        "对照组：未开启 clearReadOnly 时只读文件被跳过", r.Summary);
                else
                    Check(r.DeletedFiles == 1 && r.SkippedFiles == 0 && !File.Exists(f),
                        "clearReadOnly 清除只读属性后成功删除", r.Summary);
            }
            foreach (string f in roFiles.Where(File.Exists))
            {
                try { File.SetAttributes(f, FileAttributes.Normal); } catch { /* 仅供 finally 清理 */ }
            }

            // 7b. 模拟 wimlib 从离线注册表写入错误 DISPLAYNAME，再验证回写。
            string metaWim = Path.Combine(stage, "meta.wim");
            File.Copy(sourceWim, metaWim, true);
            if (engine.Kind == WimEngineKind.Wimlib)
            {
                WimTool.Run(engine, new[] { "info", metaWim, "1",
                    "--image-property", "DISPLAYNAME=Windows 10 Home" },
                    null, CancellationToken.None);
                byte[] mh = ReadAt(metaWim, 0, WimMetadataReader.HeaderSize);
                var ml = WimMetadataReader.LocateXml(mh);
                var mp = WimMetadataReader.Read(mh, ReadAt(metaWim, ml.Offset, (int)ml.Size), "污染后");
                Check(mp.Success && mp.Images[0].Name == "Windows 10 Home",
                    "模拟 wimlib 覆盖 DISPLAYNAME 为 Windows 10 Home",
                    mp.Success ? mp.Images[0].Name : mp.Note ?? "");

                const string expectedName = "Windows 11 家庭版";
                WimTool.SetImageProperties(engine, metaWim, 1, expectedName,
                    null, CancellationToken.None);
                byte[] nh = ReadAt(metaWim, 0, WimMetadataReader.HeaderSize);
                var nl = WimMetadataReader.LocateXml(nh);
                var np = WimMetadataReader.Read(nh, ReadAt(metaWim, nl.Offset, (int)nl.Size), "回写后");
                Check(np.Success && np.Images[0].Name == expectedName,
                    "SetImageProperties 正确回写中文版本名",
                    np.Success ? np.Images[0].Name : np.Note ?? "");
            }

            // 7c. 夺权工具本身可用（真实 TrustedInstaller ACL 场景由 --trimfull 实跑覆盖）
            string aclRoot = Path.Combine(stage, "acltool");
            string aclDir = Path.Combine(aclRoot, "SecurityHealth");
            Directory.CreateDirectory(aclDir);
            string aclFile = Path.Combine(aclDir, "locked.dll");
            File.WriteAllBytes(aclFile, new byte[4096]);
            File.SetAttributes(aclFile, File.GetAttributes(aclFile) | FileAttributes.ReadOnly);
            WindowsAclTool.EnsureDeletable(aclDir);
            var aclTarget = new CleanupTarget
            {
                Display = "acltool", Path = aclDir, RootPath = aclRoot,
                Bytes = 4096, FileCount = 1
            };
            var acl = CleanupService.DeleteTargets(new[] { aclTarget },
                removeTargetRoot: true, clearReadOnly: true);
            Check(acl.DeletedFiles == 1 && acl.SkippedFiles == 0 && !File.Exists(aclFile),
                "WindowsAclTool 夺权路径可正常清理", acl.Summary);

            // 单文件分支：真实 System32 下散落的 SecurityHealth*.dll 走这条。
            // 早期这里漏了 takeown 的 /R，/D Y 变成非法参数，9 个文件仍被拒删。
            string single = Path.Combine(stage, "acl-single.dll");
            File.WriteAllBytes(single, new byte[4096]);
            File.SetAttributes(single, File.GetAttributes(single) | FileAttributes.ReadOnly);
            WindowsAclTool.EnsureDeletable(single);
            try
            {
                var a = File.GetAttributes(single);
                if ((a & FileAttributes.ReadOnly) != 0)
                    File.SetAttributes(single, a & ~FileAttributes.ReadOnly);
                File.Delete(single);
            }
            catch { /* 由下面的断言给出结果 */ }
            Check(!File.Exists(single), "WindowsAclTool 单文件夺权分支可正常清理");

            // 7d. 主链路：输出 ISO 里的版本名必须与用户选中的一致
            Check(result.Notes.Any(n => n.Contains("版本名已回写为")),
                "主链路执行了版本名回写");
            byte[] finalHead = IsoRandomAccess.TryReadRange(output, entry, 0,
                WimMetadataReader.HeaderSize) ?? Array.Empty<byte>();
            var finalLoc = WimMetadataReader.LocateXml(finalHead);
            byte[] finalXml = IsoRandomAccess.TryReadRange(output, entry,
                finalLoc.Offset, (int)finalLoc.Size) ?? Array.Empty<byte>();
            var finalParsed = WimMetadataReader.Read(finalHead, finalXml, "输出 ISO");
            Check(finalParsed.Success && finalParsed.Images.Count == 1 &&
                  finalParsed.Images[0].Name == options.ImageName,
                "输出 ISO 内 WIM 的版本名与用户选择一致",
                finalParsed.Success && finalParsed.Images.Count > 0
                    ? finalParsed.Images[0].Name : finalParsed.Note ?? "");

            // ---------- 8. 工作目录回收 ----------
            // 真实 Win11 实跑曾留下 6.6 GB 只读目录树，导致下一次精简的空间预检失败。
            W(sb, "");
            W(sb, "-- 8. 临时工作目录回收 --");
            string recycleOut = Path.Combine(stage, "recycled.iso");
            var recycled = TrimWorkflow.Run(Clone(options, recycleOut, keepWorkDir: false),
                null, CancellationToken.None);
            Check(recycled.Success, "第二次精简成功", recycled.Error ?? "");
            Check(recycled.WorkRoot != null && !Directory.Exists(recycled.WorkRoot),
                "KeepWorkDir=false 时临时工作目录已完整回收",
                recycled.WorkRoot ?? "");
        }
        catch (Exception ex)
        {
            Check(false, "自测执行异常", $"{ex.GetType().Name}: {ex.Message}");
            W(sb, "   " + ex.StackTrace);
        }
        finally
        {
            try { Directory.Delete(stage, recursive: true); } catch { /* 占用时留给系统清理 */ }
        }

        return Finish();

        string Finish()
        {
            W(sb, "");
            W(sb, $"合计: {pass} PASS / {fail} FAIL / {pass + fail} 项");
            return sb.ToString();
        }
    }

    // ==================== --trimui ====================

    public static string RunUi(bool lightTheme)
    {
        var sb = new StringBuilder();
        int pass = 0, fail = 0;
        void Check(bool ok, string label, string detail = "")
        {
            W(sb, $"   [{(ok ? "PASS" : "FAIL")}] {label}{(detail.Length > 0 ? " — " + detail : "")}");
            if (ok) pass++; else fail++;
        }

        W(sb, "=== Basalt 精简 UI 渲染自测 ===");
        W(sb, $"主题: {(lightTheme ? "亮色" : "暗色")}");
        W(sb, "");

        string stage = Path.Combine(TestStageRoot(), $"BasaltTrimUi_{Guid.NewGuid():N}");
        Directory.CreateDirectory(stage);
        var windows = new List<Window>();
        try
        {
            if (lightTheme) ((App)Application.Current).ToggleTheme();

            var engine = WimTool.Resolve();
            Check(engine.Available, "WIM 引擎可用", engine.Label);
            if (!engine.Available) return Finish();

            string tree = Path.Combine(stage, "tree");
            BuildFakeWindowsTree(tree, small: true);
            string wim = Path.Combine(stage, "install.wim");
            WimTool.Capture(engine, tree, wim, "Windows 11 Pro", "UI fixture",
                null, CancellationToken.None);
            string iso = Path.Combine(stage, "fixture.iso");
            BuildIso(iso, wim);

            using var disk = new ContainerDiskService(iso);
            var win = new IsoInspectorWindow(disk, "fixture.iso");
            windows.Add(win);
            win.Show();
            win.UpdateLayout();
            PumpUntil(() => win.Result != null, 15000);

            Check(win.Result != null, "检查器异步加载完成");
            var versions = win.FindName("VersionList") as ListBox;
            var work = win.FindName("WorkDirBox") as TextBox;
            var keep = win.FindName("KeepOthersBox") as CheckBox;
            var engineText = win.FindName("EngineText") as TextBlock;
            var defender = win.FindName("DefenderBox") as CheckBox;
            var apply = win.FindName("ApplyTrimButton") as Button;

            Check(versions?.SelectedIndex == 0, "版本列表默认选中第一项",
                versions?.SelectedIndex.ToString() ?? "");
            Check(win.SelectedImage != null, "SelectedImage 可读",
                win.SelectedImage?.Name ?? "");
            Check(work != null && work.Text.Contains("Basalt_Wim_Work"),
                "工作目录默认指向 %TEMP%\\Basalt_Wim_Work", work?.Text ?? "");
            Check(keep?.IsChecked == true, "默认保留其它版本");
            Check((engineText?.Text ?? "").Contains("wimlib"), "显示实际使用的引擎",
                engineText?.Text ?? "");
            Check(apply?.Content.ToString() == "开始精简" && apply.IsEnabled == false,
                "未勾选项时「开始精简」禁用");

            if (defender != null) defender.IsChecked = true;
            Check(apply?.IsEnabled == true, "勾选精简项 + 选中版本后按钮激活");

            var opts = win.CreateTrimOptions();
            Check(opts.RemoveDefender && opts.KeepOtherImages && opts.ImageIndex == 1,
                "CreateTrimOptions 正确映射 UI 状态");
            Check(opts.SourceIsoPath == iso, "CreateTrimOptions 拿到源 ISO 路径",
                opts.SourceIsoPath);

            string shot = Shot(win, lightTheme ? "basalt_trim_ui_light.png" : "basalt_trim_ui.png");
            Check(shot.Length > 0, "检查器精简 UI 截图已生成", shot);

            // 进度窗口：逐个阶段渲染并截图
            var progress = new ProgressWindow();
            windows.Add(progress);
            progress.SetupTrim("精简 #1 Windows 11 Pro");
            progress.Show();
            progress.UpdateLayout();
            var bar = progress.FindName("Bar") as ProgressBar;
            var stageText = progress.FindName("StageText") as TextBlock;
            var message = progress.FindName("MessageText") as TextBlock;

            Check(stageText?.Text == "准备中", "进度窗口初始阶段为准备中", stageText?.Text ?? "");
            foreach (var (step, pct) in new[]
                     {
                         (TrimStage.Extracting, (int?)25), (TrimStage.Applying, null),
                         (TrimStage.Cleaning, (int?)60), (TrimStage.Capturing, (int?)85),
                         (TrimStage.Replacing, (int?)95)
                     })
            {
                progress.UpdateTrim(new TrimProgress
                {
                    Stage = step, Message = TrimStageNames.Of(step) + " 中",
                    Detail = "阶段进度渲染验证", Percent = pct
                });
                progress.UpdateLayout();
                Check(stageText?.Text == TrimStageNames.Of(step),
                    "阶段徽标显示「" + TrimStageNames.Of(step) + "」", stageText?.Text ?? "");
            }
            Check(bar != null && !bar.IsIndeterminate && bar.Value >= 90,
                "确定进度条生效", bar?.Value.ToString() ?? "");
            Check(message?.Text.Length > 0, "阶段消息已渲染");

            string pshot = Shot(progress,
                lightTheme ? "basalt_trim_progress_light.png" : "basalt_trim_progress.png");
            Check(pshot.Length > 0, "进度窗口截图已生成", pshot);

            progress.UpdateTrim(new TrimProgress
                { Stage = TrimStage.Done, Message = "精简版 ISO 已生成", Percent = 100 });
            Check(progress.Title == "精简完成", "完成阶段切换窗口标题", progress.Title);
        }
        catch (Exception ex)
        {
            Check(false, "UI 自测执行异常", $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            foreach (var w in windows) { try { w.Close(); } catch { } }
            try { Directory.Delete(stage, recursive: true); } catch { }
        }

        W(sb, "");
        W(sb, $"合计: {pass} PASS / {fail} FAIL / {pass + fail} 项");
        return sb.ToString();

        string Finish()
        {
            W(sb, "");
            W(sb, $"合计: {pass} PASS / {fail} FAIL / {pass + fail} 项");
            return sb.ToString();
        }
    }

    // ==================== --trimfull（真实 ISO 完整实跑） ====================

    /// <summary>
    /// 对真实 Windows 安装 ISO 完整执行精简流水线。与干跑不同，这里会真的写出一个新 ISO。
    /// 用法：Basalt.exe --trimfull "D:\path\to\windows.iso"
    /// 需要约 27 GB 临时空间；工作盘由 TrimWorkflow 自动挑选剩余空间最大的固定盘。
    /// </summary>
    public static string RunFull(string isoPath, string outputIsoPath = "")
    {
        var sb = new StringBuilder();
        int pass = 0, fail = 0;
        void Check(bool ok, string label, string detail = "")
        {
            W(sb, $"   [{(ok ? "PASS" : "FAIL")}] {label}{(detail.Length > 0 ? " — " + detail : "")}");
            if (ok) pass++; else fail++;
        }

        W(sb, "=== Basalt 真实 ISO 一键精简 完整实跑 ===");
        W(sb, $"镜像: {isoPath}");
        W(sb, "");
        try
        {
            Check(File.Exists(isoPath), "镜像文件存在");
            if (!File.Exists(isoPath)) return Finish();

            long sizeBefore = new FileInfo(isoPath).Length;
            DateTime timeBefore = File.GetLastWriteTimeUtc(isoPath);

            using var disk = new ContainerDiskService(isoPath);
            var inspection = IsoInspectorService.Inspect(disk);
            Check(inspection.InstallImagePath != null && inspection.Images.Count > 0,
                "解析真实版本列表", inspection.Images.Count + " 项");
            if (inspection.InstallImagePath == null) return Finish();

            var options = new TrimOptions
            {
                SourceIsoPath = isoPath,
                Inspection = inspection,
                ImageIndex = inspection.Images[0].Index,
                ImageName = inspection.Images[0].Name,
                ImageDescription = "Basalt 精简版（真实 ISO 验证）",
                RemoveDefender = true,
                RemoveEdge = true,
                RemoveDrivers = true,
                KeepOtherImages = false,     // 实跑只保留精简后的单版本，避免 5 次 export
                WorkRoot = "",
                // 源 ISO 所在盘可能已被前几次实跑的产物占满；--out 可把输出改写到数据盘
                OutputIsoPath = outputIsoPath,
                KeepWorkDir = false
            };

            long needed = TrimWorkflow.EstimateRequiredBytes(options, sizeBefore);
            W(sb, $"预计临时空间: {Models.FileItemFormatter.FormatSize(needed)}");
            W(sb, $"精简目标: #{options.ImageIndex} {options.ImageName}");
            W(sb, "");

            var stages = new List<string>();
            var progress = new SyncProgress<TrimProgress>(p =>
            {
                if (stages.Count == 0 || stages[^1] != p.StageText) stages.Add(p.StageText);
            });
            var sw = Stopwatch.StartNew();
            var result = TrimWorkflow.Run(options, progress, CancellationToken.None);
            sw.Stop();
            W(sb, $"耗时: {sw.ElapsedMilliseconds} ms（{sw.Elapsed.TotalMinutes:F1} 分钟）");
            W(sb, "阶段顺序: " + string.Join(" → ", stages));
            foreach (string line in result.StageLog) W(sb, "   " + line);
            foreach (string note in result.Notes) W(sb, "   备注: " + note);
            if (result.Error != null) W(sb, "   错误: " + result.Error);

            Check(result.Success, "真实 ISO 精简流水线成功", result.Error ?? "");
            if (result.Success)
            {
                Check(result.Cleanup != null && result.Cleanup.DeletedFiles > 0,
                    "真实映像中删除了文件", result.Cleanup?.Summary ?? "");
                // 修复前跳过 2943 个、只删掉 1176 个。允许极少量 reparse point /
                // 受保护硬链接残留（CleanupService 设计上不强行删链接）。
                Check(result.Cleanup != null && result.Cleanup.DeletedFiles > 3000,
                    "清理覆盖到原本被 ACL 阻挡的系统文件",
                    "已删除 " + (result.Cleanup?.DeletedFiles ?? 0) + " 个（修复前 1176 个）");
                Check(result.Cleanup != null && result.Cleanup.SkippedFiles <= 10,
                    "只读 / ACL 阻挡文件已基本清除",
                    "跳过 " + (result.Cleanup?.SkippedFiles ?? -1) + " 个（修复前 2943 个）");
                Check(result.Notes.Any(n => n.Contains("版本名已回写为")),
                    "主链路回写了版本元数据");
                Check(result.NewWimSize > 0 && result.NewWimSize < result.OriginalWimSize,
                    "新 WIM 比原始 WIM 更小", result.SizeText);
                Check(result.OutputIsoPath != null && File.Exists(result.OutputIsoPath),
                    "输出精简版 ISO", result.OutputIsoPath ?? "");
                Check(result.OutputLogicalSize == sizeBefore,
                    "输出 ISO 保留原始逻辑尺寸与 UDF 结构");
                Check(result.Notes.Any(n => n.Contains("版本列表可读")),
                    "输出 ISO 内的 WIM 版本列表可读",
                    result.Notes.FirstOrDefault(n => n.Contains("版本列表")) ?? "");
                Check(Can7ZipList(result.OutputIsoPath!),
                    "7-Zip 可正常列出精简版 ISO");
                Check(new FileInfo(isoPath).Length == sizeBefore &&
                      File.GetLastWriteTimeUtc(isoPath) == timeBefore,
                    "源 ISO 全程未被修改");

                using var outDisk = new ContainerDiskService(result.OutputIsoPath!);
                var outInspect = IsoInspectorService.Inspect(outDisk);
                Check(outInspect.InstallImagePath != null && outInspect.Images.Count > 0,
                    "精简版 ISO 可被检查器重新解析",
                    outInspect.Images.Count + " 个版本");
                foreach (var img in outInspect.Images) W(sb, $"   #{img.Index} {img.Name}");
                Check(outInspect.Images.Count > 0 &&
                      outInspect.Images[0].Name == options.ImageName,
                    "输出版本名与用户选中的版本一致",
                    "期望「" + options.ImageName + "」，实际「" +
                    (outInspect.Images.Count > 0 ? outInspect.Images[0].Name : "无") + "」");
            }
        }
        catch (Exception ex)
        {
            Check(false, "实跑异常", $"{ex.GetType().Name}: {ex.Message}");
        }

        return Finish();

        string Finish()
        {
            W(sb, "");
            W(sb, $"合计: {pass} PASS / {fail} FAIL / {pass + fail} 项");
            return sb.ToString();
        }
    }

    // ==================== --trimreal（真实 ISO 干跑，不写任何文件） ====================

    public static string RunReal(string isoPath)
    {
        var sb = new StringBuilder();
        int pass = 0, fail = 0;
        void Check(bool ok, string label, string detail = "")
        {
            W(sb, $"   [{(ok ? "PASS" : "FAIL")}] {label}{(detail.Length > 0 ? " — " + detail : "")}");
            if (ok) pass++; else fail++;
        }

        W(sb, "=== Basalt 真实 ISO 精简链路干跑（不写入任何文件） ===");
        W(sb, $"镜像: {isoPath}");
        W(sb, "");
        try
        {
            Check(File.Exists(isoPath), "镜像文件存在");
            if (!File.Exists(isoPath)) return Finish();

            long sizeBefore = new FileInfo(isoPath).Length;
            DateTime timeBefore = File.GetLastWriteTimeUtc(isoPath);
            var sw = Stopwatch.StartNew();

            using var disk = new ContainerDiskService(isoPath);
            var r = IsoInspectorService.Inspect(disk);
            Check(r.InstallImagePath != null && r.Images.Count > 0,
                "检查器解析真实版本列表", r.Images.Count + " 项");
            if (r.InstallImagePath == null) return Finish();

            byte[] head = IsoRandomAccess.TryReadRange(isoPath, r.InstallImagePath, 0,
                WimMetadataReader.HeaderSize) ?? Array.Empty<byte>();
            var loc = WimMetadataReader.LocateXml(head);
            Check(loc.Success, "WIM 头定位尾部 XML", loc.Note ?? "");
            if (!loc.Success) return Finish();

            var extent = IsoExtentService.Find(isoPath, r.InstallImagePath,
                r.InstallImageSize, loc.Offset, CancellationToken.None);
            Check(extent != null, "在真实 ISO 中定位 install.wim 物理 extent");
            if (extent != null)
            {
                W(sb, $"   {extent.Display} · {extent.Note}");
                Check(extent.Offset > 0 && extent.Length == r.InstallImageSize,
                    "extent 长度与检查器一致", extent.Length + " 字节");
                Check(extent.Verified, "extent 连续性通过 UDF 流交叉校验");
                Check(extent.Offset + loc.Offset + loc.Size <= sizeBefore,
                    "尾部 XML 落在该 extent 内");
            }

            var options = new TrimOptions
            {
                SourceIsoPath = isoPath,
                Inspection = r,
                ImageIndex = r.Images[0].Index,
                ImageName = r.Images[0].Name,
                RemoveEdge = true
            };
            long needed = TrimWorkflow.EstimateRequiredBytes(options, sizeBefore);
            W(sb, $"预计临时空间需求: {Models.FileItemFormatter.FormatSize(needed)}");
            Check(needed > sizeBefore, "空间预估大于单个 ISO（含展开与副本）");

            string workRootNote;
            try
            {
                string root = TrimWorkflow.ResolveWorkRoot("", needed);
                workRootNote = "自动选定工作目录 " + root;
                Check(true, "本机存在足够空间，可直接执行精简", root);
            }
            catch (InvalidOperationException ex)
            {
                workRootNote = ex.Message;
                Check(ex.Message.Contains("可用空间不足"),
                    "空间不足时给出明确报错并提示更换工作盘", ex.Message[..Math.Min(120, ex.Message.Length)]);
            }
            W(sb, "   " + workRootNote);

            Check(new FileInfo(isoPath).Length == sizeBefore &&
                  File.GetLastWriteTimeUtc(isoPath) == timeBefore,
                "干跑未改动源 ISO");
            sw.Stop();
            W(sb, $"总耗时: {sw.ElapsedMilliseconds} ms");
            Check(sw.ElapsedMilliseconds < 30000, "真实 ISO 定位在 30 秒内完成",
                sw.ElapsedMilliseconds + " ms");
        }
        catch (Exception ex)
        {
            Check(false, "干跑异常", $"{ex.GetType().Name}: {ex.Message}");
        }

        return Finish();

        string Finish()
        {
            W(sb, "");
            W(sb, $"合计: {pass} PASS / {fail} FAIL / {pass + fail} 项");
            return sb.ToString();
        }
    }

    // ==================== 夹具 ====================

    /// <summary>
    /// 夹具落在剩余空间最大的固定盘。系统盘曾被瞬时写满导致 wimlib export 报
    /// "No space left on device"，因此不再无条件用 %TEMP%。
    /// 可用 BASALT_TEST_ROOT 显式指定。
    /// </summary>
    private static string TestStageRoot()
    {
        string? configured = Environment.GetEnvironmentVariable("BASALT_TEST_ROOT");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            Directory.CreateDirectory(configured);
            return configured;
        }

        string best = Path.GetTempPath();
        long bestFree = -1;
        foreach (var d in DriveInfo.GetDrives()
                     .Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
        {
            if (d.AvailableFreeSpace <= bestFree) continue;
            bestFree = d.AvailableFreeSpace;
            best = d.RootDirectory.FullName;
        }
        string root = Path.Combine(best, "BasaltSelfTest");
        Directory.CreateDirectory(root);
        return root;
    }

    private sealed class SyncProgress<T> : IProgress<T>
    {
        private readonly Action<T> _action;
        public SyncProgress(Action<T> action) => _action = action;
        public void Report(T value) => _action(value);
    }

    private static TrimOptions Clone(TrimOptions o, string output, bool? keepWorkDir = null) => new()
    {
        SourceIsoPath = o.SourceIsoPath,
        Inspection = o.Inspection,
        ImageIndex = o.ImageIndex,
        ImageName = o.ImageName,
        ImageDescription = o.ImageDescription,
        RemoveDefender = o.RemoveDefender,
        RemoveEdge = o.RemoveEdge,
        RemoveDrivers = o.RemoveDrivers,
        KeepOtherImages = o.KeepOtherImages,
        WorkRoot = o.WorkRoot,
        OutputIsoPath = output,
        KeepWorkDir = keepWorkDir ?? o.KeepWorkDir
    };

    /// <summary>模拟 Windows 映像：可删内容与必须保留的基准文件混在一起。</summary>
    private static void BuildFakeWindowsTree(string root, bool small = false)
    {
        int unit = small ? 4096 : 192 * 1024;
        var rnd = new Random(20261115);
        void Put(string rel, int bytes)
        {
            string p = Path.Combine(root, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            var data = new byte[bytes];
            rnd.NextBytes(data);
            File.WriteAllBytes(p, data);
        }

        Put(@"Windows\System32\notepad.exe", 2048);          // 必须保留
        Put(@"Windows\System32\kernel32.dll", 4096);          // 必须保留
        Put(@"Windows\System32\SecurityHealth\SecurityHealthService.exe", unit);
        Put(@"Windows\System32\SecurityHealth\SecurityHealthSystray.exe", unit / 2);
        // 真实 Win11 里 SecurityHealth 还有一堆散落在 System32 下的同名文件
        Put(@"Windows\System32\SecurityHealthAgent.dll", unit / 8);
        Put(@"Windows\System32\SecurityHealthCore.dll", unit / 8);
        Put(@"Windows\SysWOW64\SecurityHealthSSO.dll", unit / 8);
        Put(@"Windows\SystemApps\Microsoft.Windows.SecHealthUI_cw5n1h2txyewy\app.exe", unit / 2);
        Put(@"Windows\SystemApps\Microsoft.MicrosoftEdge_8wekyb3d8bbwe\microsoftedge.exe", unit);
        Put(@"Windows\SystemApps\Microsoft.XboxGamingOverlay_8wekyb3d8bbwe\gamebar.exe", unit / 2);
        Put(@"Program Files (x86)\Microsoft\Edge\Application\msedge.exe", unit);
        Put(@"Program Files (x86)\Microsoft\EdgeUpdate\Update\msedgeupdate.exe", unit / 2);
        Put(@"Program Files (x86)\Microsoft\EdgeWebView\Application\msedgewebview2.exe", unit / 2);
        Put(@"ProgramData\Microsoft\Windows Defender\Platform\mpengine.dll", unit / 2);
        Put(@"Windows\System32\DriverStore\FileRepository\foo.inf_amd64_aaaa1111\foo.sys", unit / 4);
        Put(@"Windows\System32\DriverStore\FileRepository\foo.inf_amd64_bbbb2222\foo.sys", unit / 4);
        Put(@"Windows\System32\DriverStore\FileRepository\bar.inf_amd64_cccc3333\bar.sys", unit / 4);
        Put(@"Windows\System32\DriverStore\FileRepository\prnms001.inf_amd64_dddd4444\prn.sys", unit / 4);
        Put(@"Windows\System32\DriverStore\FileRepository\mdmags64.inf_amd64_eeee5555\mdm.sys", unit / 4);

        // 让同组驱动有明确的新旧关系，去重时保留 bbbb2222
        string oldDir = Path.Combine(root, @"Windows\System32\DriverStore\FileRepository\foo.inf_amd64_aaaa1111");
        string newDir = Path.Combine(root, @"Windows\System32\DriverStore\FileRepository\foo.inf_amd64_bbbb2222");
        Directory.SetLastWriteTimeUtc(oldDir, new DateTime(2020, 1, 1));
        Directory.SetLastWriteTimeUtc(newDir, new DateTime(2025, 1, 1));
    }

    private static void BuildIso(string isoPath, string wimPath)
    {
        var builder = new CDBuilder
        {
            UseJoliet = true,
            VolumeIdentifier = "BASALT_TRIM_FIXTURE"
        };
        builder.AddDirectory("sources");
        builder.AddFile(@"sources\install.wim", wimPath);
        builder.AddFile(@"sources\setup.exe", Encoding.UTF8.GetBytes("fixture setup"));
        builder.AddFile(@"readme.txt", Encoding.UTF8.GetBytes("Basalt trim self-test fixture"));
        builder.Build(isoPath);
    }

    private static byte[] ReadAt(string path, long offset, int count)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            1 << 16, FileOptions.RandomAccess);
        fs.Position = offset;
        var b = new byte[count];
        fs.ReadExactly(b);
        return b;
    }

    private static byte[] Sha256(string path)
    {
        using var fs = File.OpenRead(path);
        return SHA256.HashData(fs);
    }

    private static bool Can7ZipList(string isoPath)
    {
        string? sevenZip = ContainerDetector.SevenZipPath;
        if (sevenZip == null) return false;
        var psi = new ProcessStartInfo(sevenZip)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("l");
        psi.ArgumentList.Add("-slt");
        psi.ArgumentList.Add("-ba");
        psi.ArgumentList.Add("-sccUTF-8");
        psi.ArgumentList.Add(isoPath);
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 7-Zip。");
        string output = proc.StandardOutput.ReadToEnd();
        proc.StandardError.ReadToEnd();
        proc.WaitForExit(60000);
        return proc.ExitCode == 0 && output.Contains("sources\\install.wim",
            StringComparison.OrdinalIgnoreCase);
    }

    private static void PumpUntil(Func<bool> done, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (!done() && sw.ElapsedMilliseconds < timeoutMs)
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(50), DispatcherPriority.Background,
                (s, e) => { ((DispatcherTimer)s!).Stop(); frame.Continue = false; },
                Application.Current.Dispatcher);
            timer.Start();
            Dispatcher.PushFrame(frame);
        }
    }

    private static string Shot(Window win, string fileName)
    {
        win.Show();
        win.UpdateLayout();
        win.Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
        int w = Math.Max(1, (int)Math.Ceiling(win.ActualWidth));
        int h = Math.Max(1, (int)Math.Ceiling(win.ActualHeight));
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(win);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        string path = Path.Combine(Path.GetTempPath(), fileName);
        using var fs = File.Create(path);
        encoder.Save(fs);
        return path;
    }
}
