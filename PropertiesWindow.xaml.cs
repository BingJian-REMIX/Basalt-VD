using System.Windows;
using Basalt.Models;

namespace Basalt;

/// <summary>文件属性弹窗：展示完整路径、大小、创建/修改/访问时间与文件属性。</summary>
public partial class PropertiesWindow : Window
{
    public PropertiesWindow(FileMetadata meta, System.Windows.Media.ImageSource? icon)
    {
        InitializeComponent();

        NameText.Text = meta.Name;
        PathText.Text = meta.FullPath;
        SizeText.Text = meta.IsDirectory ? "<目录>" : meta.SizeText;
        CreatedText.Text = meta.CreatedText;
        ModifiedText.Text = meta.ModifiedText;
        AccessedText.Text = meta.AccessedText;
        AttributesText.Text = string.IsNullOrEmpty(meta.AttributesText) ? "—" : meta.AttributesText;

        if (icon != null)
            IconImage.Source = icon;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
