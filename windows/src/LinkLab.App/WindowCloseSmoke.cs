using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace LinkLab.App;

internal static class WindowCloseSmoke
{
    private static void Check(bool condition, string message) { if (!condition) throw new IOException("Window close smoke: " + message); }
    private static MainWindow Window(ShellViewModel vm, Func<Task>? close = null)
    {
        var window = close is null ? new MainWindow(vm, false) : new MainWindow(vm, close);
        window.WindowStartupLocation = WindowStartupLocation.Manual; window.Left = -16000; window.Top = 0; window.ShowActivated = false; window.ShowInTaskbar = false;
        window.Show(); return window;
    }
    private static TaskCompletionSource Closed(Window window)
    {
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Closed += (_, _) => closed.TrySetResult(); return closed;
    }
    private static void ClickClose(MainWindow window) => ((Button)window.FindName("CloseWindowButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static string FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port.ToString();
    }
    internal static async Task Run(string output)
    {
        var report = new List<string>();
        // The original bug occurs when all cleanup Tasks are already complete.
        var idleVm = new ShellViewModel(false); var idle = Window(idleVm); var idleClosed = Closed(idle);
        ClickClose(idle);
        Check(!idleClosed.Task.IsCompleted, "idle close must leave the initial Closing event before closing again");
        await idleClosed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        report.Add("PASS idle window: actual title-bar X click, synchronously completed cleanup, Closed event.");

        var delayedVm = new ShellViewModel(false);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); int cleanupCalls = 0;
        var delayed = Window(delayedVm, async () => { cleanupCalls++; await release.Task; await delayedVm.Close(false); });
        var delayedClosed = Closed(delayed);
        try
        {
            ClickClose(delayed); delayed.Close(); delayed.Close();
            await delayed.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Check(!delayedClosed.Task.IsCompleted && !delayed.IsEnabled, "repeated close requests must wait for cleanup");
            Check(cleanupCalls == 1, "cleanup must run only once");
        }
        finally { release.TrySetResult(); }
        await delayedClosed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        report.Add("PASS delayed cleanup: repeated Close requests remain cancelled until cleanup completes; cleanup runs once.");

        var failedVm = new ShellViewModel(false);
        var failed = Window(failedVm, () => throw new IOException("Expected cleanup failure for verification"));
        var failedClosed = Closed(failed); ClickClose(failed);
        await failedClosed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(failedVm.Hub.Notice == "Expected cleanup failure for verification", "cleanup failure must still be reported");
        await failedVm.Close(false);
        report.Add("PASS cleanup exception: error reported, deferred close still finishes without re-entering Closing.");

        var liveVm = new ShellViewModel(false); var live = Window(liveVm); var liveClosed = Closed(live);
        try
        {
            string port = FreePort(); liveVm.Simulator.Port = port; liveVm.Master.Port = port;
            liveVm.Simulator.ToggleCommand.Execute(null);
            for (int i = 0; i < 100 && !liveVm.Simulator.Running; i++) await Task.Delay(20);
            Check(liveVm.Simulator.Running, "local Modbus simulator did not start");
            await liveVm.Master.Execute(); Check(liveVm.Master.Rows.Count == 10, "local master read failed");
            liveVm.Master.Interval = "50"; liveVm.Master.PollCommand.Execute(null);
            Check(!liveVm.Master.CanConfigure, "polling did not start");
            liveVm.Network.Port = port; liveVm.Network.ConnectCommand.Execute(null);
            for (int i = 0; i < 100 && !liveVm.Network.Connected; i++) await Task.Delay(20);
            Check(liveVm.Network.Connected, "local TCP connection did not open");
            var pixels = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, new byte[16], 8); pixels.Freeze();
            var pin = liveVm.Quick.PinImage(pixels, false); pin.WindowStartupLocation = WindowStartupLocation.Manual; pin.Left = -16000; pin.Top = 0; pin.ShowInTaskbar = false; pin.ShowActivated = false; pin.Topmost = false; pin.Show();
            ClickClose(live);
            await liveClosed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Check(!liveVm.Simulator.Running && !liveVm.Master.HasSession && liveVm.Master.CanConfigure && !liveVm.Network.Connected && !pin.IsVisible, "active resources survived window close");
            var probe = new TcpListener(IPAddress.Loopback, int.Parse(port));
            try { probe.Start(); } finally { probe.Stop(); }
            report.Add("PASS active connections: title-bar X stops Modbus master polling, simulator, TCP client and pinned image; listener port released.");
        }
        finally { await liveVm.Close(false); }
        File.WriteAllLines(Path.Combine(output, "window-close-result.txt"), report);
    }
}
