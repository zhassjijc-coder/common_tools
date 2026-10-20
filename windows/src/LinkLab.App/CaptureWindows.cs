using System.Windows;
using System.Windows.Controls;

namespace LinkLab.App;
public sealed class OcrTextWindow : Window
{
    public OcrTextWindow(string text, Action<string> copy)
    {
        Title = "设备联调工具 · 文字识别结果"; Width = 700; Height = 480; MinWidth = 440; MinHeight = 260; Topmost = true; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        SetResourceReference(BackgroundProperty, "Bg"); SetResourceReference(ForegroundProperty, "Text");
        var root = new DockPanel { Margin = new Thickness(20) };
        var header = new TextBlock { Text = "识别结果", FontSize = 21, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 15) }; DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        var edit = new TextBox { Text = text, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalContentAlignment = VerticalAlignment.Top, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontSize = 15 };
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
        var button = new Button { Content = "复制全部文字", Padding = new Thickness(18, 9, 18, 9) }; button.Click += (_, _) => copy(edit.Text); bar.Children.Add(button); bar.Children.Add(new TextBlock { Text = "可先编辑，再复制。复杂排版请核对。", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0), FontSize = 12 }); DockPanel.SetDock(bar, Dock.Bottom); root.Children.Add(bar); root.Children.Add(edit); Content = root;
    }
}
