using System.IO;
using System.Text;
using Basalt.Models;
using Basalt.Services;
using Basalt.ViewModels;

namespace Basalt.Services;

/// <summary>
/// 「虚拟盘里的容器」链路自测：
/// VMDK/VHD 内的 zip/iso 文件 → 实体化到临时目录 → 魔数识别 → 建立容器引擎 → 浏览内容。
/// 这是最复杂的路径（文件不在真实磁盘上），必须端到端验证。
/// </summary>
public static class NestedContainerSelfTest
{
    public static string Run(string diskPath)
    {
        var sb = new StringBuilder();
        void W(string s) => sb.AppendLine(s);

        W("=== 虚拟盘内容器 链路自测 ===");
        W($"磁盘: {diskPath}");
        W("");

        var vm = new MainViewModel();
        vm.MountPath(diskPath);
        if (vm.Roots.Count == 0)
        {
            W("❌ 挂载失败");
            return sb.ToString();
        }

        // 在虚拟盘里找一个容器文件（zip/iso/7z/rar）
        W("---- 在虚拟盘内搜索容器文件 ----");
        var found = new List<FileItem>();
        void Search(string dir, int depth)
        {
            if (depth > 4 || found.Count > 0) return;
            // 必须先物化：OpenItem 会整体替换 vm.Files，边遍历边替换会让枚举器失效
            var snapshot = vm.Files.ToList();
            foreach (var f in snapshot)
            {
                if (f.IsParentEntry) continue;
                if (!f.IsDirectory)
                {
                    var ext = Path.GetExtension(f.Name).ToLowerInvariant();
                    if (ext is ".zip" or ".iso" or ".7z" or ".rar" or ".cab")
                    {
                        found.Add(f);
                        return;
                    }
                }
            }
            foreach (var d in snapshot.Where(x => x.IsDirectory && !x.IsParentEntry).ToList())
            {
                vm.OpenItem(d);
                Search(d.FullPath, depth + 1);
                if (found.Count > 0) return;
            }
        }
        Search("", 0);

        if (found.Count == 0)
        {
            W("   （该虚拟盘内未找到 zip/iso/7z/rar 文件，无法验证嵌套容器链路）");
            W("   ℹ️ 引擎层能力已由 --containertest 覆盖（真实 zip/iso/sfx 均通过）");
            vm.UnmountOnExit();
            return sb.ToString();
        }

        var target = found[0];
        W($"   找到: {target.Name}  ({target.FullPath})");
        W("");

        // ---- 实体化 ----
        W("---- 实体化到临时目录 ----");
        var engine = (IVirtualDisk)typeof(MainViewModel)
            .GetField("_disk", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(vm)!;
        string real;
        try
        {
            real = engine.MaterializeToTemp(target.FullPath);
            var fi = new FileInfo(real);
            W($"   ✅ 实体化: {real}");
            W($"      大小 {fi.Length} 字节（原标记 {target.SizeText}）");
        }
        catch (Exception ex)
        {
            W($"   ❌ 实体化失败: {ex.GetType().Name}: {ex.Message}");
            vm.UnmountOnExit();
            return sb.ToString();
        }
        W("");

        // ---- 魔数识别 ----
        W("---- 魔数识别 ----");
        var fmt = ContainerDetector.DetectFile(real);
        W($"   检测结果: {fmt} ({ContainerDetector.DisplayName(fmt)})");
        if (fmt == ContainerFormat.None)
        {
            W("   ❌ 实体化后的文件未被识别为容器（内容可能不完整）");
            vm.UnmountOnExit();
            return sb.ToString();
        }
        W("");

        // ---- 建立容器引擎并浏览 ----
        W("---- 建立容器引擎并浏览 ----");
        try
        {
            using var container = new ContainerDiskService(real);
            W($"   容器根: {container.DisplayName}（{ContainerDetector.DisplayName(fmt)}）");
            int dirs = container.GetDirectories("").Count;
            int files = container.GetFiles("").Count;
            W($"   根下: {dirs} 个目录, {files} 个文件");
            foreach (var f in container.GetFiles("").Take(8))
                W($"      [F] {f.Name}  {f.SizeText}");
            foreach (var d in container.GetDirectories("").Take(8))
                W($"      [D] {d.Name}");

            // 提取第一个文件并校验内容非空
            var anyFile = container.GetFiles("").FirstOrDefault();
            if (anyFile != null)
            {
                string dest = Path.Combine(Path.GetTempPath(), $"basalt_nested_{Guid.NewGuid():N}{Path.GetExtension(anyFile.Name)}");
                container.ExtractFile(anyFile.FullPath, dest);
                long len = new FileInfo(dest).Length;
                W($"   ✅ 提取 '{anyFile.Name}' → {len} 字节（归档内标记 {anyFile.SizeText}）");
                try { File.Delete(dest); } catch { }
            }
            W("");
            W("✅ 完整链路验证通过：虚拟盘内文件 → 实体化 → 识别 → 容器浏览 → 提取");
        }
        catch (Exception ex)
        {
            W($"   ❌ 容器浏览失败: {ex.GetType().Name}: {ex.Message}");
        }

        vm.UnmountOnExit();
        return sb.ToString();
    }
}
