using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;

namespace LinkLab.App;

public partial class MainWindow : Window
{
    private readonly ShellViewModel _vm;
    private readonly Func<Task> _closeSession;
    private bool _cleanupStarted, _allowClose;
    public MainWindow(ShellViewModel vm, bool save)
        : this(vm, () => vm.Close(save)) { }
    internal MainWindow(ShellViewModel vm, Func<Task> closeSession)
    { InitializeComponent(); _vm = vm; _closeSession = closeSession; DataContext = vm; Closing += OnClosing; }
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        if (_cleanupStarted) return;
        _cleanupStarted = true; IsEnabled = false;
        try { await _closeSession(); } catch (Exception ex) { _vm.Hub.Error(ex); }
        // Idle cleanup can complete synchronously. Always leave the original Closing event
        // before requesting the final close, and keep repeated requests cancelled meanwhile.
        await Dispatcher.InvokeAsync(() => { _allowClose = true; Close(); }, DispatcherPriority.Normal);
    }
}
