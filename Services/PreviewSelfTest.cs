using System.IO;
using System.IO.Compression;
using System.Text;
using Basalt.Common;
using Basalt.Services;

namespace Basalt.Services;

/// <summary>
/// 预览分发器 / 7z 编码 / 归档转 VHDX 的无界面回归自测。
/// 自带夹具（文本 / GBK 文本 / 代码 / 图片 / 二进制 / 中文目录名），不依赖用户文件。
/// </summary>
public static class PreviewSelfTest
{
    private const string OnePixelPng =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4z8DwHwAFAAH/q842iQAAAABJRU5ErkJggg==";

    public static string Run(string? externalPath = null)
    {
        var sb = new StringBuilder();
        var failures = new List<string>();
        void W(string s) => sb.AppendLine(s);
        void Check(bool ok, string label, string detail = "")
        {
            W($"   {(ok ? "✅" : "❌")} {label}{(detail.Length > 0 ? " — " + detail : "")}");
            if (!ok) failures.Add(label);
        }

        W("=== Basalt 预览 / 编码 / VHDX 自测 ===");
        W("");

        string stage = Path.Combine(Path.GetTempPath(), $"BasaltPreviewTest_{Guid.NewGuid():N}");
        string zipPath = Path.Combine(Path.GetTempPath(), $"basalt_preview_{Guid.NewGuid():N}.zip");
        string vhdxPath = Path.Combine(Path.GetTempPath(), $"basalt_preview_{Guid.NewGuid():N}.vhdx");
        Directory.CreateDirectory(stage);

        try
        {
            // ---- 夹具 ----
            string utf8Text = "Basalt 内部预览：中文 UTF-8 ✓\n第二行\n";
            string gbkText = "Basalt 内部预览：中文 GBK ✓\n";
            string codeText = "public class Demo { /* C# */ }\n";
            var binary = new byte[8192];
            binary[0] = (byte)'M'; binary[1] = (byte)'Z';
            for (int i = 2; i < binary.Length; i++) binary[i] = (byte)(i & 0xFF);

            Directory.CreateDirectory(Path.Combine(stage, "中文目录"));
            File.WriteAllText(Path.Combine(stage, "readme.txt"), utf8Text, new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(stage, "gbk.txt"), gbkText, Encoding.GetEncoding(54936));
            File.WriteAllText(Path.Combine(stage, "code.cs"), codeText, new UTF8Encoding(false));
            File.WriteAllBytes(Path.Combine(stage, "data.bin"), binary);
            File.WriteAllBytes(Path.Combine(stage, "pic.png"), Convert.FromBase64String(OnePixelPng));
            File.WriteAllText(Path.Combine(stage, "中文目录", "中文文件.txt"), "层级中文内容\n", new UTF8Encoding(false));

            ZipFile.CreateFromDirectory(stage, zipPath);
            W($"夹具归档: {zipPath}");
            W("");

            // ---- 分类 ----
            W("---- 预览分发分类 ----");
            Check(PreviewDispatcher.Classify("readme.txt") == PreviewKind.Text, ".txt → Text");
            Check(PreviewDispatcher.Classify("code.cs") == PreviewKind.Text, ".cs → Text");
            Check(PreviewDispatcher.Classify("a.json") == PreviewKind.Text, ".json → Text");
            Check(PreviewDispatcher.Classify("pic.png") == PreviewKind.Image, ".png → Image");
            Check(PreviewDispatcher.Classify("a.jpg") == PreviewKind.Image, ".jpg → Image");
            Check(PreviewDispatcher.Classify("a.ico") == PreviewKind.Image, ".ico → Image");
            Check(PreviewDispatcher.Classify("a.dll") == PreviewKind.Hex, ".dll → Hex");
            Check(PreviewDispatcher.Classify("a.exe") == PreviewKind.Hex, ".exe → Hex");
            Check(PreviewDispatcher.Classify("a.bin") == PreviewKind.Hex, ".bin → Hex");
            Check(PreviewDispatcher.Classify("x.unknown") == PreviewKind.Hex, "未知扩展名 → Hex（不偷懒外抛）");
            Check(PreviewDispatcher.Classify("manual.pdf") == PreviewKind.External, ".pdf → 外部兜底");
            Check(PreviewDispatcher.Classify("doc.docx") == PreviewKind.External, ".docx → 外部兜底");
            W("");

            // ---- 容器引擎：中文名 + OpenRead 纯内存流 ----
            W("---- 容器引擎（7z l -slt -sccUTF-8）----");
            using var disk = new ContainerDiskService(zipPath);
            var dirs = disk.GetDirectories("");
            var files = disk.GetFiles("");
            Check(dirs.Any(d => d.Name == "中文目录"), "中文目录名无乱码", string.Join(",", dirs.Select(d => d.Name)));
            Check(files.Any(f => f.Name == "中文文件.txt") == false, "中文子文件不串到根目录");
            Check(files.All(f => f.Size > 0), "FileItem.Size 已填充", files.Count + " 个直接文件");
            W("");

            W("---- OpenRead 纯内存流 + 三种内部预览 ----");
            using (var s = disk.OpenRead("readme.txt"))
            {
                var r = PreviewDispatcher.ReadText(s);
                Check(r.Text.Contains("中文 UTF-8"), "UTF-8 文本读回", r.EncodingName);
                Check(!r.Truncated, "小文件未触发截断");
            }
            using (var s = disk.OpenRead("gbk.txt"))
            {
                var r = PreviewDispatcher.ReadText(s);
                Check(r.Text.Contains("中文 GBK"), "GBK 文本自动回退解码", r.EncodingName);
            }
            using (var s = disk.OpenRead("pic.png"))
            {
                var img = PreviewDispatcher.LoadImage(s);
                Check(img.PixelWidth > 0 && img.PixelHeight > 0, "Stream → BitmapImage 纯内存渲染",
                    $"{img.PixelWidth}×{img.PixelHeight}");
            }
            using (var s = disk.OpenRead("data.bin"))
            {
                var head = PreviewDispatcher.ReadHead(s, 4096);
                Check(head.Length == 4096 && head[0] == 'M' && head[1] == 'Z', "前 4KB 读取");
                string hex = PreviewDispatcher.FormatHex(head[..64]);
                Check(hex.Contains("4D 5A") && hex.Contains("|MZ"), "Hex 格式化（偏移+HEX+ASCII）");
            }
            using (var s = disk.OpenRead("中文目录\\中文文件.txt"))
            {
                var r = PreviewDispatcher.ReadText(s);
                Check(r.Text.Contains("层级中文内容"), "归档内层级中文路径读取");
            }
            W("");

            W("---- 分发器（无界面模式不建窗口）----");
            try
            {
                string d1 = PreviewDispatcher.Preview(disk, "readme.txt", "readme.txt");
                string d2 = PreviewDispatcher.Preview(disk, "pic.png", "pic.png");
                string d3 = PreviewDispatcher.Preview(disk, "data.bin", "data.bin");
                Check(d1.Contains("文本") && d2.Contains("图片") && d3.Contains("十六进制"),
                    "三种预览均命中内部窗口", d1 + " | " + d2 + " | " + d3);
            }
            catch (Exception ex) { Check(false, "分发器执行", ex.Message); }
            W("");

            // ---- 归档转 VHDX ----
            W("---- 压缩包 → 真 VHDX ----");
            try
            {
                string summary = ArchiveToVhdxService.Build(zipPath, vhdxPath);
                Check(File.Exists(vhdxPath), "VHDX 已生成", new FileInfo(vhdxPath).Length + " 字节物理占用");
                var listed = ArchiveToVhdxService.ListAllFiles(vhdxPath);
                Check(listed.Count == 6, "回读 VHDX 文件数一致", listed.Count + " 个: " + string.Join(", ", listed));
                Check(listed.Any(p => p.Contains("中文文件")), "VHDX 内中文路径保留");
                W("   " + summary);
            }
            catch (Exception ex) { Check(false, "归档转 VHDX", ex.GetType().Name + ": " + ex.Message); }
            W("");

            // ---- 外部镜像（可选）----
            if (!string.IsNullOrWhiteSpace(externalPath) && File.Exists(externalPath))
            {
                W($"---- 外部镜像 OpenRead: {externalPath} ----");
                try
                {
                    using var ext = DiskEngineFactory.Create(externalPath, readOnly: true);
                    var f = ext.GetFiles("").FirstOrDefault(x => !x.IsDirectory);
                    if (f == null) W("   （根目录没有文件，跳过）");
                    else
                    {
                        using var s = ext.OpenRead(f.FullPath);
                        var head = PreviewDispatcher.ReadHead(s, 16);
                        Check(head.Length > 0, $"{f.Name} 可读（{head.Length} 字节）");
                    }
                    ext.Dispose();
                }
                catch (Exception ex) { Check(false, "外部镜像 OpenRead", ex.Message); }
                W("");
            }

            // ---- 临时文件清理 / Chert 路径 ----
            W("---- 辅助项 ----");
            string temp = TempFileRegistry.NewPath("probe.txt");
            File.WriteAllText(temp, "x");
            TempFileRegistry.Cleanup();
            Check(!File.Exists(temp), "临时文件退出清理生效");
            Check(Directory.Exists(ChertIntegration.GameRoot), "Chert 工作目录可解析", ChertIntegration.GameRoot);
            W("");
        }
        catch (Exception ex)
        {
            Check(false, "自测主流程", ex.ToString());
        }
        finally
        {
            try { if (File.Exists(zipPath)) File.Delete(zipPath); } catch { }
            try { if (File.Exists(vhdxPath)) File.Delete(vhdxPath); } catch { }
            try { Directory.Delete(stage, true); } catch { }
        }

        W(failures.Count == 0
            ? "=== 全部通过 PASS ==="
            : $"=== 失败 {failures.Count} 项 FAIL: {string.Join(" / ", failures)} ===");
        return sb.ToString();
    }

    /// <summary>对指定归档单独做转 VHDX 验证。</summary>
    public static string RunVhdx(string archivePath)
    {
        var sb = new StringBuilder();
        string outPath = Path.Combine(Path.GetTempPath(), $"basalt_vhdxtest_{Guid.NewGuid():N}.vhdx");
        try
        {
            sb.AppendLine("=== Basalt 归档转 VHDX 专项自测 ===");
            sb.AppendLine($"归档: {archivePath}");
            if (!File.Exists(archivePath)) { sb.AppendLine("❌ 归档不存在"); return sb.ToString(); }
            sb.AppendLine(ArchiveToVhdxService.Build(archivePath, outPath));
            var listed = ArchiveToVhdxService.ListAllFiles(outPath);
            sb.AppendLine($"物理大小: {new FileInfo(outPath).Length} 字节");
            sb.AppendLine($"回读文件数: {listed.Count}");
            foreach (var p in listed.Take(30)) sb.AppendLine("   " + p);
            sb.AppendLine(listed.Count > 0 ? "=== PASS ===" : "=== FAIL ===");
        }
        catch (Exception ex) { sb.AppendLine($"❌ {ex}"); sb.AppendLine("=== FAIL ==="); }
        finally { try { if (File.Exists(outPath)) File.Delete(outPath); } catch { } }
        return sb.ToString();
    }
}
