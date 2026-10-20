using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace LinkLab.App;

public static class AutoScroll
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached("Enabled", typeof(bool), typeof(AutoScroll), new(false, Changed));
    public static readonly DependencyProperty PausedProperty = DependencyProperty.RegisterAttached("Paused", typeof(bool), typeof(AutoScroll), new(false));
    private static readonly DependencyProperty HandlerProperty = DependencyProperty.RegisterAttached("Handler", typeof(NotifyCollectionChangedEventHandler), typeof(AutoScroll));
    private static readonly HashSet<DataGrid> Pending = [];
    public static bool GetEnabled(DependencyObject obj) => (bool)obj.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject obj, bool value) => obj.SetValue(EnabledProperty, value);
    public static bool GetPaused(DependencyObject obj) => (bool)obj.GetValue(PausedProperty);
    public static void SetPaused(DependencyObject obj, bool value) => obj.SetValue(PausedProperty, value);
    private static void Changed(DependencyObject obj, DependencyPropertyChangedEventArgs e)
    {
        if (obj is not DataGrid grid) return;
        grid.Loaded += (_, _) => Attach(grid); grid.Unloaded += (_, _) => Detach(grid);
        if (grid.IsLoaded) Attach(grid);
    }
    private static void Attach(DataGrid grid)
    {
        Detach(grid);
        NotifyCollectionChangedEventHandler handler = (_, _) =>
        {
            if (GetPaused(grid) || !grid.IsLoaded || !Pending.Add(grid)) return;
            grid.Dispatcher.BeginInvoke(() =>
            {
                Pending.Remove(grid);
                if (grid.IsLoaded && !GetPaused(grid) && grid.Items.Count > 0) grid.ScrollIntoView(grid.Items[^1]);
            }, DispatcherPriority.Background);
        };
        grid.SetValue(HandlerProperty, handler); ((INotifyCollectionChanged)grid.Items).CollectionChanged += handler;
    }
    private static void Detach(DataGrid grid)
    {
        if (grid.GetValue(HandlerProperty) is NotifyCollectionChangedEventHandler handler) ((INotifyCollectionChanged)grid.Items).CollectionChanged -= handler;
        grid.ClearValue(HandlerProperty); Pending.Remove(grid);
    }
}
