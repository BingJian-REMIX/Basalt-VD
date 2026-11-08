using System.IO;
using System.Text;
using Basalt.Models;

namespace Basalt.Services;

/// <summary>挂载验证模式：Basalt.exe --mounttest &lt;vhd路径&gt;。挂载→分配盘符→列根目录→卸载。</summary>
public static class MountTest
{
    public static string Run(string path)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"MountTest: {path}");
        sb.AppendLine($"时间: {System.DateTime.Now:HH:mm:ss}");
        try
        {
            using var disk = new VhdDiskService(path, readOnly: false);
            char letter = disk.DriveLetter ?? '?';
            sb.AppendLine($"结果: 成功, 盘符 = {letter}:\\");
            sb.AppendLine($"只读: {disk.MountedReadOnly}");

            string root = $"{letter}:\\";
            var dirs = disk.GetDirectories(root);
            var files = disk.GetFiles(root);
            sb.AppendLine($"根目录条目: {dirs.Count} 目录 + {files.Count} 文件");
            foreach (var d in dirs.Take(10)) sb.AppendLine($"  [DIR ] {d.Name}");
            foreach (var f in files.Take(10)) sb.AppendLine($"  [FILE] {f.Name} ({f.SizeText})");

            sb.AppendLine("正在卸载...");
        }
        catch (Exception ex)
        {
            sb.AppendLine($"失败: {ex.GetType().Name}: {ex.Message}");
            if (ex.InnerException != null)
                sb.AppendLine($"  内部异常: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
        }
        sb.AppendLine("完成。");
        return sb.ToString();
    }
}
