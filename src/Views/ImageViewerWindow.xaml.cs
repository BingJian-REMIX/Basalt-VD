using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace Basalt;

/// <summary>
/// 内部图片预览：BitmapImage 由纯内存 Stream 构造（VMDK / 容器均不落地），
/// 鼠标滚轮缩放，双击适应窗口。
/// </summary>
public partial class ImageViewerWindow : Window
{
    private const double MinZoom = 0.05;
    private const double MaxZoom = 20.0;

    private readonly BitmapImage _image;
    private double _zoom = 1.0;

    public ImageViewerWindow(string fileName, BitmapImage image)
    {
        InitializeComponent();
        _image = image;
        Title = $"图片预览 — {fileName}";
        HeaderText.Text = fileName;
        ImageView.Source = image;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e) => FitToWindow();

    private void FitToWindow()
    {
        if (_image.PixelWidth <= 0 || _image.PixelHeight <= 0) return;
        double w = Scroller.ViewportWidth - 20, h = Scroller.ViewportHeight - 20;
        if (w <= 0 || h <= 0) return;
        SetZoom(Math.Min(1.0, Math.Min(w / _image.PixelWidth, h / _image.PixelHeight)));
    }

    private void Window_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        SetZoom(_zoom * (e.Delta > 0 ? 1.15 : 1.0 / 1.15));
        e.Handled = true;
    }

    private void Window_MouseDoubleClick(object sender, MouseButtonEventArgs e) => FitToWindow();

    private void SetZoom(double value)
    {
        _zoom = Math.Clamp(value, MinZoom, MaxZoom);
        ZoomTransform.ScaleX = _zoom;
        ZoomTransform.ScaleY = _zoom;
        StatusText.Text = $"{_image.PixelWidth}×{_image.PixelHeight} · 缩放 {_zoom * 100:F0}% · 滚轮缩放 / 双击适应窗口";
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
        base.OnKeyDown(e);
    }
}
