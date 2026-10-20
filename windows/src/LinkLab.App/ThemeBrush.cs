using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;

namespace LinkLab.App;

/// <summary>Bind a resource name while retaining live theme updates.</summary>
public static class ThemeBrush
{
    public static readonly DependencyProperty KeyProperty = DependencyProperty.RegisterAttached(
        "Key", typeof(string), typeof(ThemeBrush), new PropertyMetadata(null, Changed));
    public static string? GetKey(DependencyObject value) => (string?)value.GetValue(KeyProperty);
    public static void SetKey(DependencyObject value, string key) => value.SetValue(KeyProperty, key);
    private static void Changed(DependencyObject value, DependencyPropertyChangedEventArgs e)
    {
        if (value is FrameworkElement element && e.NewValue is string key)
            element.SetResourceReference(value is Shape ? Shape.StrokeProperty : TextBlock.ForegroundProperty, key);
    }
}
