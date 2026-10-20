using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace LinkLab.App;

// WPF's inner ScrollViewer consumes the wheel even when its content cannot move.
// Let a parent handle the same gesture once the inner table / text box reaches an edge.
public static class ScrollChain
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(ScrollChain), new PropertyMetadata(false, Changed));
    public static bool GetEnabled(DependencyObject element) => (bool)element.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject element, bool value) => element.SetValue(EnabledProperty, value);
    private static void Changed(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        if (element is not ScrollViewer viewer) return;
        if ((bool)args.OldValue) viewer.PreviewMouseWheel -= OnWheel;
        if ((bool)args.NewValue) viewer.PreviewMouseWheel += OnWheel;
    }
    private static void OnWheel(object sender, MouseWheelEventArgs args)
    {
        if (args.Handled || args.Delta == 0 || sender is not ScrollViewer outer) return;
        ScrollViewer? inner = null;
        for (var node = args.OriginalSource as DependencyObject; node is not null && node != outer; node = Parent(node))
        {
            if (node is not ScrollViewer viewer) continue;
            inner ??= viewer;
            // Preserve native scrolling within long logs and populated tables.
            if (viewer.VerticalScrollBarVisibility != ScrollBarVisibility.Disabled &&
                (args.Delta < 0 ? viewer.VerticalOffset < viewer.ScrollableHeight - 0.01 : viewer.VerticalOffset > 0.01)) return;
        }
        if (inner is null) return;
        args.Handled = true;
        outer.RaiseEvent(new MouseWheelEventArgs(args.MouseDevice, args.Timestamp, args.Delta)
        { RoutedEvent = Mouse.MouseWheelEvent, Source = outer });
    }
    private static DependencyObject? Parent(DependencyObject node) => node is Visual or Visual3D
        ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
}
