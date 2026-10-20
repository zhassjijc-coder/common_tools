using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LinkLab.App;

public sealed class PinnedImageWindow : Window
{
    private readonly BitmapSource _image;
    private readonly TextBlock _scaleLabel = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 6, 0), FontSize = 12 };
    private readonly ScrollViewer _viewport;
    private readonly Image _picture;
    private double _scale;
    internal double ImageScale => _scale;
    public PinnedImageWindow(BitmapSource image, Action<BitmapSource> copy, Action<BitmapSource> ocr)
    {
        _image = image;
        Title = "设备联调工具 · 贴图 · 拖动移动 / 滚轮缩放 / Ctrl+滚轮透明度 / Ctrl+0 原始比例";
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.CanResizeWithGrip; Topmost = true; ShowInTaskbar = false;
        MinWidth = 260; MinHeight = 150; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        BorderThickness = new Thickness(1); SetResourceReference(BackgroundProperty, "Card"); SetResourceReference(ForegroundProperty, "Text"); SetResourceReference(BorderBrushProperty, "Line");
        var root = new DockPanel(); root.SetResourceReference(Panel.BackgroundProperty, "Card");
        var toolbar = new WrapPanel(); toolbar.SetResourceReference(Panel.BackgroundProperty, "Card");
        foreach (var (label, action) in new (string, Action)[]
        {
            ("复制", () => copy(image)), ("识别", () => ocr(image)), ("−", () => SetScale(_scale / 1.15)),
            ("+", () => SetScale(_scale * 1.15)), ("100%", () => SetScale(1)), ("适应", Fit), ("关闭", Close)
        })
        {
            var button = new Button { Content = label, Padding = new Thickness(8, 5, 8, 5), Margin = new Thickness(2), FontSize = 12, Focusable = false };
            button.Click += (_, _) => action(); toolbar.Children.Add(button);
        }
        toolbar.Children.Add(_scaleLabel); DockPanel.SetDock(toolbar, Dock.Top); root.Children.Add(toolbar);
        var tips = new TextBlock { Text = "拖动图片移动 · 滚轮缩放 · Ctrl+滚轮透明度 · 双击 / Esc 关闭", FontSize = 10, Padding = new Thickness(7, 3, 7, 4), TextWrapping = TextWrapping.Wrap };
        tips.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); DockPanel.SetDock(tips, Dock.Bottom); root.Children.Add(tips);
        _picture = new Image { Source = image, Stretch = Stretch.Fill, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        _viewport = new ScrollViewer { Content = _picture, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, CanContentScroll = false };
        root.Children.Add(_viewport); Content = root;
        double initial = Math.Min(1, Math.Min(850d / image.PixelWidth, 600d / image.PixelHeight));
        Width = Math.Max(320, image.PixelWidth * initial + 4); Height = Math.Max(200, image.PixelHeight * initial + 80);
        SetScale(initial, false);
        Loaded += (_, _) => SetScale(_scale);
        DpiChanged += (_, e) => SetScale(_scale, dpiOverride: e.NewDpi);
        MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) Close(); else DragMove(); };
        PreviewMouseWheel += (_, e) =>
        {
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) Opacity = Math.Clamp(Opacity + (e.Delta > 0 ? .1 : -.1), .2, 1);
            else SetScale(_scale * (e.Delta > 0 ? 1.15 : 1 / 1.15));
            e.Handled = true;
        };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
            else if (e.Key == Key.D0 && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) SetScale(1);
            else if (e.Key is Key.Add or Key.OemPlus) SetScale(_scale * 1.15);
            else if (e.Key is Key.Subtract or Key.OemMinus) SetScale(_scale / 1.15);
        };
        ToolTip = Title;
    }
    private void Fit()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        SetScale(Math.Min(Math.Max(50, _viewport.ActualWidth - 8) * dpi.DpiScaleX / _image.PixelWidth,
            Math.Max(50, _viewport.ActualHeight - 8) * dpi.DpiScaleY / _image.PixelHeight), false);
    }
    private void SetScale(double scale, bool resize = true, DpiScale? dpiOverride = null)
    {
        var dpi = dpiOverride ?? VisualTreeHelper.GetDpi(this);
        _scale = Math.Clamp(scale, .1, 8); _picture.Width = _image.PixelWidth * _scale / dpi.DpiScaleX; _picture.Height = _image.PixelHeight * _scale / dpi.DpiScaleY;
        _scaleLabel.Text = $"{_scale:P0}";
        if (!resize) return;
        double maxWidth = Math.Max(MinWidth, SystemParameters.WorkArea.Width * .9), maxHeight = Math.Max(MinHeight, SystemParameters.WorkArea.Height * .9);
        // Include the native resize frame and the measured toolbar/footer, so a
        // 100% image does not acquire scrollbars merely because of window chrome.
        double chromeWidth = IsLoaded && _viewport.ActualWidth > 0 ? Math.Max(4, ActualWidth - _viewport.ActualWidth) + 2 : 24;
        double chromeHeight = IsLoaded && _viewport.ActualHeight > 0 ? Math.Max(80, ActualHeight - _viewport.ActualHeight) + 2 : 110;
        Width = Math.Clamp(_picture.Width + chromeWidth, MinWidth, maxWidth); Height = Math.Clamp(_picture.Height + chromeHeight, MinHeight, maxHeight);
    }
    internal void ScaleForVerification(double scale) => SetScale(scale);
}
