using System.Diagnostics;
using System.IO;
using System.Text;

namespace Basalt.Services;

/// <summary>
/// 本轮三项功能的无界面回归自测：递归提取、深度清理（含权限异常）、空白 VHDX（MBR/GPT）。
/// 全部夹具自建，不依赖用户文件与管理员权限。
/// </summary>
public static class DeepFeatureSelfTest
{
    private sealed class ProgressCollector : IProgress<ExtractProgress>
    {
        public readonly List<ExtractProgress> Items = new();
        public void Report(ExtractProgress value) { lock (Items) Items.Add(value); }
    }

    /// <param name="tempRoot">
    /// 夹具根目录。默认系统临时目录；系统盘空间不足时可传入数据盘目录
    /// （`Basalt.exe --deeptest D:\some\dir`），夹具结束会自行删除。
    /// </param>
    public static string Run(string? tempRoot = null)
    {
        var sb = new StringBuilder();
        int pass = 0, fail = 0;
        void W(string s) => sb.AppendLine(s);
        void Check(bool ok, string label, string detail = "")
        {
            W($"   [{(ok ? "PASS" : "FAIL")}] {label}{(detail.Length > 0 ? " — " + detail : "")}");
            if (ok) pass++; else fail++;
        }

        W("=== Basalt 深度功能自测（递归提取 / 深度清理 / 新建 VHDX） ===");
        W("");

        string testRoot = string.IsNullOrWhiteSpace(tempRoot) ? Path.GetTempPath() : tempRoot;
        string stage = Path.Combine(testRoot, $"BasaltDeepTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(stage);
        string? sevenZip = ContainerDetector.SevenZipPath;

        try
        {
            if (sevenZip == null)
            {
                Check(false, "7-Zip 可用", "未找到 7z.exe");
                return Finish();
            }

            // ================= 1. 递归提取 =================
            W("-- 1. 递归提取（OpenRead 流重建目录树） --");
            string src = Path.Combine(stage, "src");
            Directory.CreateDirectory(Path.Combine(src, "子目录", "深层"));
            Directory.CreateDirectory(Path.Combine(src, "空目录"));

            byte[] a = Encoding.UTF8.GetBytes("Basalt 递归提取 A\n");
            byte[] b = new byte[4096];
            for (int i = 0; i < b.Length; i++) b[i] = (byte)(i & 0xFF);
            byte[] c = Encoding.UTF8.GetBytes("中文路径 / 深层文件 C");
            File.WriteAllBytes(Path.Combine(src, "a.txt"), a);
            File.WriteAllBytes(Path.Combine(src, "子目录", "b.bin"), b);
            File.WriteAllBytes(Path.Combine(src, "子目录", "深层", "c.txt"), c);

            string treeZip = Path.Combine(stage, "tree.zip");
            Check(MakeZip(sevenZip, treeZip, stage, "src"), "7z 打包夹具目录树");

            using (var disk = new ContainerDiskService(treeZip))
            {
                string outDir = Path.Combine(stage, "out");
                var collector = new ProgressCollector();
                ExtractResult result = RecursiveExtractor.Extract(disk, "src", outDir, collector);

                string root = Path.Combine(outDir, "src");
                Check(result.Files == 3, "提取 3 个文件", result.Summary);
                Check(result.Directories >= 3, "重建子目录结构", $"{result.Directories} 个目录");
                Check(result.Skipped == 0, "无跳过项", string.Join(" | ", result.Errors));
                Check(File.Exists(Path.Combine(root, "a.txt")) &&
                      File.ReadAllBytes(Path.Combine(root, "a.txt")).SequenceEqual(a), "a.txt 字节一致");
                Check(File.Exists(Path.Combine(root, "子目录", "b.bin")) &&
                      File.ReadAllBytes(Path.Combine(root, "子目录", "b.bin")).SequenceEqual(b), "中文子目录 b.bin 字节一致");
                Check(File.Exists(Path.Combine(root, "子目录", "深层", "c.txt")) &&
                      File.ReadAllBytes(Path.Combine(root, "子目录", "深层", "c.txt")).SequenceEqual(c), "深层 c.txt 字节一致");
                Check(result.Bytes == a.Length + b.Length + c.Length, "写入字节数正确",
                    $"{result.Bytes} / {a.Length + b.Length + c.Length}");
                Check(collector.Items.Any(p => p.Phase == ExtractPhase.Scanning), "进度：扫描 Loading 阶段");
                Check(collector.Items.Any(p => p.Phase == ExtractPhase.Copying), "进度：确定进度条阶段");
                Check(collector.Items.Any(p => p.Phase == ExtractPhase.Done), "进度：完成阶段");
                Check(collector.Items.Last().FilesDone == collector.Items.Last().FilesTotal,
                    "进度终值收敛", $"{collector.Items.Last().FilesDone}/{collector.Items.Last().FilesTotal}");
            }

            // ================= 2. 深度清理扫描 =================
            W("");
            W("-- 2. 深度清理扫描（三引擎通用 / 只读引擎禁用删除） --");
            string recycle = Path.Combine(stage, "$Recycle.Bin", "S-1-5-21");
            string temp = Path.Combine(stage, "Windows", "Temp");
            string download = Path.Combine(stage, "Windows", "SoftwareDistribution", "Download");
            Directory.CreateDirectory(recycle);
            Directory.CreateDirectory(temp);
            Directory.CreateDirectory(download);
            File.WriteAllBytes(Path.Combine(recycle, "old.dat"), new byte[1234]);
            File.WriteAllBytes(Path.Combine(temp, "t.tmp"), new byte[2048]);
            File.WriteAllBytes(Path.Combine(download, "update.cab"), new byte[4096]);

            string cleanZip = Path.Combine(stage, "clean.zip");
            Check(MakeZip(sevenZip, cleanZip, stage, "$Recycle.Bin", "Windows"), "7z 打包三个清理目标");

            using (var disk = new ContainerDiskService(cleanZip))
            {
                var targets = CleanupService.Scan(disk);
                Check(targets.Count == 3, "扫描到 3 个目标目录", string.Join(", ", targets.Select(t => t.Display)));
                Check(targets.Sum(t => t.Bytes) == 1234 + 2048 + 4096, "递归大小统计正确",
                    Models.FileItemFormatter.FormatSize(targets.Sum(t => t.Bytes)));
                Check(targets.Sum(t => t.FileCount) == 3, "文件数统计正确");
                Check(targets.Any(t => t.Display.Contains("系统更新缓存") && t.Bytes == 4096),
                    "系统更新缓存 4.0 KB 单独列出");
                Check(!CleanupService.CanDelete(disk), "归档引擎删除被禁用");
                Check(CleanupService.DeleteBlockedReason(disk).Contains("只读"), "禁用原因文案明确");
            }

            // ================= 3. 删除权限异常捕获 =================
            W("");
            W("-- 3. 真实目录删除与权限异常捕获 --");
            string realRoot = Path.Combine(stage, "realroot");
            string realRecycle = Path.Combine(realRoot, "$Recycle.Bin");
            Directory.CreateDirectory(realRecycle);
            File.WriteAllBytes(Path.Combine(realRecycle, "a.bin"), new byte[100]);
            string readOnly = Path.Combine(realRecycle, "ro.bin");
            File.WriteAllBytes(readOnly, new byte[50]);
            File.SetAttributes(readOnly, FileAttributes.ReadOnly);

            var delTargets = new[]
            {
                new CleanupTarget { Display = "回收站", Path = realRecycle, RootPath = realRoot }
            };
            CleanupResult del = CleanupService.DeleteTargets(delTargets);
            Check(del.DeletedFiles >= 1, "普通文件被删除", $"{del.DeletedFiles} 个");
            Check(del.SkippedFiles >= 1, "只读 / 占用文件被安全跳过而非抛异常", $"{del.SkippedFiles} 个");
            Check(File.Exists(readOnly), "只读文件仍在（未误删）");

            string outside = Path.Combine(stage, "outside");
            Directory.CreateDirectory(outside);
            string outsideFile = Path.Combine(outside, "keep.bin");
            File.WriteAllBytes(outsideFile, new byte[10]);
            var badTarget = new CleanupTarget { Display = "越界", Path = outside, RootPath = realRoot };
            CleanupResult bad = CleanupService.DeleteTargets(new[] { badTarget });
            Check(bad.DeletedFiles == 0 && File.Exists(outsideFile), "盘符根之外的路径被拒绝");
            Check(bad.Errors.Count > 0, "越界拒绝有明确记录");

            // 符号链接（需要开发者模式/管理员；无权限时记录为跳过，不算失败）
            string link = Path.Combine(realRecycle, "link");
            try
            {
                Directory.CreateSymbolicLink(link, outside);
                var linkResult = CleanupService.DeleteTargets(delTargets);
                Check(File.Exists(outsideFile), "链接目录被跳过，未删到盘外文件");
                Check(linkResult.Errors.Count > 0, "链接跳过有明确记录");
            }
            catch (Exception ex)
            {
                W($"   [SKIP] 本机无法创建符号链接（{ex.GetType().Name}），跳过链接防逃逸验证");
            }

            // ================= 4. 空白 VHDX（MBR / GPT） =================
            W("");
            W("-- 4. 新建空白 NTFS VHDX --");
            foreach (var style in new[] { PartitionStyle.Mbr, PartitionStyle.Gpt })
            {
                string path = Path.Combine(stage, $"blank_{style}.vhdx");
                string label = "TEST" + style.ToString().ToUpperInvariant();
                string summary = EmptyVhdxService.Create(path, label, 1, style);
                var info = EmptyVhdxService.Inspect(path);
                Check(info.PartitionStyleName == style.ToString().ToUpperInvariant(),
                    $"{style} 分区表回读正确", info.PartitionStyleName);
                Check(info.FileSystem.Contains("NTFS", StringComparison.OrdinalIgnoreCase),
                    $"{style} 文件系统为 NTFS", info.FileSystem);
                Check(info.Label == label, $"{style} 卷标正确", info.Label);
                Check(ArchiveToVhdxService.ListAllFiles(path).Count == 0,
                    $"{style} 分区为空白（无用户文件）");
                Check(new FileInfo(path).Length > 0, $"{style} VHDX 已落盘", summary);
                try { File.Delete(path); } catch { }
            }

            return Finish();
        }
        catch (Exception ex)
        {
            Check(false, "自测未抛异常", ex.ToString());
            return Finish();
        }
        finally
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(stage, "*", SearchOption.AllDirectories))
                    try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
                Directory.Delete(stage, recursive: true);
            }
            catch { /* 占用时留给系统清理 */ }
        }

        string Finish()
        {
            W("");
            W($"TOTAL: {pass} PASS / {fail} FAIL");
            return sb.ToString();
        }
    }

    /// <summary>调用 7z 把工作目录下的若干目录打成 zip，保留相对层级。</summary>
    private static bool MakeZip(string sevenZip, string zipPath, string workDir, params string[] sources)
    {
        var psi = new ProcessStartInfo(sevenZip)
        {
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("a");
        psi.ArgumentList.Add(zipPath);
        psi.ArgumentList.Add("-r");
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add("-bso0");
        psi.ArgumentList.Add("-bsp0");
        foreach (string s in sources) psi.ArgumentList.Add(s);

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 7-Zip。");
        proc.StandardOutput.ReadToEnd();
        proc.StandardError.ReadToEnd();
        proc.WaitForExit(120000);
        return proc.ExitCode == 0 && File.Exists(zipPath);
    }
}
