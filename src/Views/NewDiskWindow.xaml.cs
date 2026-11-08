using System.IO;
using System.Windows;
using Basalt.Services;
using Microsoft.Win32;

namespace Basalt;

public partial class NewDiskWindow : Window
{
    /// <summary>创建成功后的摘要，供主窗口状态栏显示。</summary>
    public string? CreatedSummary { get; private set; }

    public NewDiskWindow()
    {
        InitializeComponent();
        NameBox.Text = "BasaltDisk";
        CapacityBox.Text = "8";
        MbrRadio.IsChecked = true;
        PathBox.Text = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "BasaltDisk.vhdx");
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "保存新的虚拟磁盘",
            Filter = "VHDX 虚拟磁盘 (*.vhdx)|*.vhdx",
            FileName = SafeFileName(NameBox.Text) + ".vhdx",
            OverwritePrompt = true
        };
        if (dialog.ShowDialog() == true)
            PathBox.Text = dialog.FileName;
    }

    /// <summary>磁盘名称去掉 Windows 文件名非法字符。</summary>
    private static string SafeFileName(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return string.IsNullOrWhiteSpace(name) ? "BasaltDisk" : name.Trim();
    }

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        HintText.Text = "";

        if (!int.TryParse(CapacityBox.Text.Trim(), out int gb) || gb < 1 || gb > 2048)
        {
            HintText.Text = "容量必须是 1 – 2048 之间的整数（GB）。";
            return;
        }
        string path = PathBox.Text.Trim();
        if (path.Length == 0)
        {
            HintText.Text = "请选择保存路径。";
            return;
        }

        try
        {
            var style = GptRadio.IsChecked == true ? PartitionStyle.Gpt : PartitionStyle.Mbr;
            string summary = EmptyVhdxService.Create(path, NameBox.Text, gb, style);
            CreatedSummary = summary;
            if (!App.Headless)
                MessageBox.Show(this, summary, "Basalt — 创建成功", MessageBoxButton.OK, MessageBoxImage.Information);
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            HintText.Text = "创建失败：" + ex.Message;
        }
    }
}
