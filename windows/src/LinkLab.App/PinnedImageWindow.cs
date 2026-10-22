using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LinkLab.App;

public sealed class PinnedImageWindow : Window
{
    private readonly BitmapSource _image;
    private readonly Image _picture;
    private readonly TextBlock _zoom;
    private double _scale;
    internal double ImageScale => _scale;
    public PinnedImageWindow(BitmapSource image, Action<BitmapSource> copy, Action<BitmapSource> ocr)
    {
        _image = image;
        Title = "设备联调工具 · 贴图 · 拖动移动 / 滚轮缩放 / Ctrl+滚轮透明度 / 双击或 Esc 关闭";
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; Topmost = true; ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        BorderThickness = new Thickness(1); SetResourceReference(BackgroundProperty, "Card"); SetResourceReference(ForegroundProperty, "Text"); SetResourceReference(BorderBrushProperty, "Line");
        var root = new DockPanel(); root.SetResourceReference(Panel.BackgroundProperty, "Card");
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Height = 38 };
        foreach (var (label, action) in new (string, Action)[] { ("复制", () => copy(image)), ("识别", () => ocr(image)) })
        {
            var button = new Button { Content = label, Padding = new Thickness(8, 5, 8, 5), Margin = new Thickness(2), FontSize = 12, Focusable = false };
            button.Click += (_, _) => action(); toolbar.Children.Add(button);
        }
        DockPanel.SetDock(toolbar, Dock.Top); root.Children.Add(toolbar);
        _zoom = new TextBlock { Height = 24, FontSize = 11, Padding = new Thickness(6, 3, 6, 3), TextAlignment = TextAlignment.Center };
        _zoom.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); DockPanel.SetDock(_zoom, Dock.Bottom); root.Children.Add(_zoom);
        _picture = new Image { Source = image, Stretch = Stretch.Fill, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        root.Children.Add(_picture); Content = root;
        SetScale(Math.Min(1, Math.Min(850d / image.PixelWidth, 600d / image.PixelHeight)));
        Loaded += (_, _) => SetScale(_scale);
        DpiChanged += (_, e) => SetScale(_scale, e.NewDpi);
        MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) Close(); else DragMove(); };
        PreviewMouseWheel += (_, e) =>
        {
            if (e.Delta == 0) return;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) Opacity = Math.Clamp(Opacity + (e.Delta > 0 ? .1 : -.1), .2, 1);
            else SetScale(_scale * Math.Pow(1.15, e.Delta / 120d));
            e.Handled = true;
        };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        ToolTip = Title;
    }
    private void SetScale(double scale, DpiScale? dpiOverride = null)
    {
        var dpi = dpiOverride ?? VisualTreeHelper.GetDpi(this);
        var handle = new WindowInteropHelper(this).Handle;
        var screen = handle == IntPtr.Zero ? System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position) : System.Windows.Forms.Screen.FromHandle(handle);
        double maxWidth = screen.WorkingArea.Width * .9 / dpi.DpiScaleX;
        double maxHeight = screen.WorkingArea.Height * .9 / dpi.DpiScaleY;
        // One scale controls both dimensions. Limit the scale itself before
        // sizing the window, so the entire image stays visible without scrolling.
        double fit = Math.Min(Math.Max(1, maxWidth - 2) * dpi.DpiScaleX / _image.PixelWidth,
            Math.Max(1, maxHeight - 64) * dpi.DpiScaleY / _image.PixelHeight);
        double maximum = Math.Min(8, fit);
        _scale = Math.Clamp(scale, Math.Min(.01, maximum), maximum);
        _picture.Width = _image.PixelWidth * _scale / dpi.DpiScaleX;
        _picture.Height = _image.PixelHeight * _scale / dpi.DpiScaleY;
        Width = Math.Max(126, _picture.Width + 2); Height = _picture.Height + 64;
        _zoom.Text = $"{_scale * 100:0.#}% · 滚轮缩放";
    }
    internal void ScaleForVerification(double scale) => SetScale(scale);
}
