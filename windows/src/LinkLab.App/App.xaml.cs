using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace LinkLab.App;

public partial class App : Application
{
    private SingleInstance? _instance;
    private DesktopLifetime? _desktop;
    public static void ApplyTheme(bool light)
    {
        string[] keys = ["Bg", "Sidebar", "Card", "Input", "Line", "Text", "Muted", "Accent", "Hover", "Selected", "AccentInk", "Hero", "GridAlternate", "TextSelection", "GridHeader", "ScrollHandle", "ErrorAccent"];
        string[] colors = light
            ? ["#F7F8FB", "#FFFFFF", "#FFFFFF", "#FAFBFD", "#E2E7EF", "#202B3D", "#617086", "#355FED", "#F0F4FC", "#EAF0FF", "#FFFFFF", "#FFFFFF", "#FAFBFD", "#CDDEFF", "#F3F5F9", "#C7D0DE", "#C03545"]
            : ["#10151F", "#141B27", "#1B2433", "#131C29", "#2B374A", "#EDF3FF", "#96A7BE", "#50CFC2", "#2A394D", "#203E44", "#092E31", "#1C353C", "#182130", "#386F83", "#1B2433", "#3B4D65", "#F38B8B"];
        for (int i = 0; i < keys.Length; i++) Current.Resources[keys[i]] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[i]));
        string[] accentKeys = ["SerialAccent", "NetworkAccent", "ProtocolAccent", "SimAccent", "BuilderAccent", "InspectAccent", "PlcAccent", "QuickAccent", "MqttAccent", "VirtualAccent"];
        string[] accents = light ? ["#087F77", "#345FD0", "#7543BB", "#A35B14", "#AE3E72", "#19738D", "#487C31", "#A65C21", "#7543BB", "#167591"] : ["#50CFC2", "#88ABFF", "#B3A0F5", "#E8B272", "#E795B7", "#75C7DE", "#9ED08C", "#F0B77F", "#C0A2FF", "#75C7DE"];
        for (int i = 0; i < accentKeys.Length; i++) Current.Resources[accentKeys[i]] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(accents[i]));
        Current.Resources[SystemColors.WindowBrushKey] = Current.Resources["Card"];
        Current.Resources[SystemColors.WindowTextBrushKey] = Current.Resources["Text"];
        Current.Resources[SystemColors.HighlightBrushKey] = Current.Resources["Selected"];
    }
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        bool smoke = e.Args.Contains("--smoke-test");
        bool closeSmoke = e.Args.Contains("--close-smoke-test");
        bool systemSmoke = e.Args.Contains("--system-smoke-test");
        bool plcConnectSmoke = e.Args.Contains("--plc-connect-smoke-test");
        bool captureNetworkSmoke = e.Args.Contains("--capture-network-smoke-test");
        bool driverCheck = e.Args.Contains("--driver-check");
        bool verification = smoke || closeSmoke || systemSmoke || plcConnectSmoke || driverCheck || captureNetworkSmoke;
        string? output = verification ? Path.GetFullPath(e.Args.Last()) : null;
        StringWriter? trace = null;
        if (verification)
        {
            Directory.CreateDirectory(output!); trace = new StringWriter();
            PresentationTraceSources.DataBindingSource.Listeners.Add(new TextWriterTraceListener(trace));
            PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        }
        DispatcherUnhandledException += (_, args) =>
        {
            if (verification) { args.Handled = true; File.WriteAllText(Path.Combine(output!, "failure.txt"), args.Exception.ToString()); Shutdown(1); }
            else { WriteCrash(args.Exception); MessageBox.Show("应用遇到了未处理错误，详情已写入 %LOCALAPPDATA%\\LinkLab\\crash.log。\n" + args.Exception.Message, "设备联调工具", MessageBoxButton.OK, MessageBoxImage.Error); }
        };
        try
        {
            bool background = e.Args.Contains("--background");
            if (!verification)
            {
                _instance = new SingleInstance();
                if (!_instance.IsPrimary) { if (!background) _instance.RequestShow(); Shutdown(0); return; }
                ShutdownMode = ShutdownMode.OnMainWindowClose;
            }
            if (closeSmoke || systemSmoke || plcConnectSmoke || driverCheck || captureNetworkSmoke)
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                if (captureNetworkSmoke)
                {
                    await CaptureNetworkSmoke.Run(output!);
                    PresentationTraceSources.DataBindingSource.Flush();
                    File.WriteAllText(Path.Combine(output!, "bindings.log"), trace!.ToString());
                    Shutdown(trace.ToString().Length == 0 ? 0 : 2); return;
                }
                if (closeSmoke) await WindowCloseSmoke.Run(output!);
                else if (systemSmoke) await SystemSettingsSmoke.Run(output!);
                else if (plcConnectSmoke)
                {
                    await PlcConnectionSmoke.Run(output!);
                    PresentationTraceSources.DataBindingSource.Flush();
                    File.WriteAllText(Path.Combine(output!, "bindings.log"), trace!.ToString());
                    Shutdown(trace.ToString().Length == 0 ? 0 : 2); return;
                }
                else await VirtualSerialDriverCheck.Run(e.Args, output!);
                Shutdown(0); return;
            }
            var vm = new ShellViewModel(!smoke, smoke ? SystemSettingsSmoke.CreateRegistration() : null);
            var window = new MainWindow(vm, !smoke); MainWindow = window;
            if (smoke) { window.WindowStartupLocation = WindowStartupLocation.Manual; window.Left = -16000; window.Top = 0; window.ShowActivated = false; window.ShowInTaskbar = false; }
            if (smoke) { _desktop = new DesktopLifetime(vm, window, activate: false); _desktop.Start(background: false); }
            else
            {
                _desktop = new DesktopLifetime(vm, window);
                _instance!.Listen(() => { if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(() => _desktop?.ShowMain()); });
                _desktop.Start(background);
                if (e.Args.Contains("--virtual-serial")) vm.Navigate("virtualserial");
            }
            if (smoke)
            {
                // Exercise actual view-model actions over loopback before capturing each page.
                var portProbe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
                portProbe.Start(); string testPort = ((System.Net.IPEndPoint)portProbe.LocalEndpoint).Port.ToString(); portProbe.Stop();
                vm.Simulator.Port = testPort; vm.Master.Port = testPort;
                vm.Simulator.ToggleCommand.Execute(null);
                for (int i = 0; i < 50 && !vm.Simulator.Running; i++) await Task.Delay(50);
                if (!vm.Simulator.Running) throw new IOException("UI smoke: simulator failed to start: " + vm.Hub.Notice);
                await vm.Master.Execute();
                if (vm.Master.Rows.Count != 10 || vm.Master.Rows[0].Raw != "2300") throw new IOException("UI smoke: master read failed");
                vm.Master.Query.FunctionIndex = 5; vm.Master.Query.Address = "5"; vm.Master.Query.Values = "4321";
                await vm.Master.Execute();
                if (vm.Simulator.Memory.Get(LinkLab.Core.DataArea.HoldingRegisters, 5) != 4321) throw new IOException("UI smoke: write failed");
                vm.Simulator.Refresh();
                vm.Master.Query.FunctionIndex = 2; vm.Master.Query.Address = "0"; vm.Master.Query.Quantity = "10";
                vm.Master.Interval = "50"; vm.Master.PollCommand.Execute(null); await Task.Delay(170); vm.Master.PollCommand.Execute(null);
                for (int i = 0; i < 50 && !vm.Master.CanConfigure; i++) await Task.Delay(20);
                if (!vm.Master.CanConfigure) throw new IOException("UI smoke: poll did not stop");
                vm.Master.Interval = "1000"; await vm.Master.Execute();
                vm.Network.Host = "127.0.0.1"; vm.Network.Port = testPort; vm.Network.ConnectCommand.Execute(null);
                for (int i = 0; i < 50 && !vm.Network.Connected; i++) await Task.Delay(20);
                if (!vm.Network.Connected) throw new IOException("UI smoke: raw network connection failed");
                vm.Workbench.Mode = 1; vm.Workbench.SelectedSession = vm.Hub.Sessions.Single(); vm.Workbench.Generate(); vm.Workbench.SendCommand.Execute(null);
                for (int i = 0; i < 50; i++) { await Task.Delay(20); vm.Hub.Flush(); if (vm.Hub.Logs.Any(x => x.Source.StartsWith("TCP →") && x.Direction == "RX")) break; }
                if (!vm.Hub.Logs.Any(x => x.Source.StartsWith("TCP →") && x.Direction == "RX")) throw new IOException("UI smoke: generated frame round trip failed");
                vm.Network.Sender.Payload = vm.Workbench.Frame; vm.Network.Sender.Interval = "50";
                vm.Network.Sender.PeriodicCommand.Execute(null); await Task.Delay(170); await vm.Network.Sender.Stop();
                vm.Network.Sender.Interval = "1000"; vm.Hub.Flush();
                vm.Monitor.Direction = "TX";
                if (vm.Monitor.View.Cast<LogRow>().Any(x => x.Direction != "TX")) throw new IOException("UI smoke: filter failed");
                vm.Monitor.Direction = "全部";
                await NetworkConfigSmoke.Run(vm, window, output!);
                await ExtendedSmoke.Run(vm, window, output!);
                await VirtualSerialSmoke.Run(vm, output!);
                await NavigationScrollSmoke.Run(vm, window, output!);
                foreach (var key in vm.Navigation.Select(x => x.Key))
                {
                    vm.Navigate(key); await Capture(window, Path.Combine(output!, key + "-light.png"));
                    var scroll = Descendants<ScrollViewer>(window).FirstOrDefault(x => ReferenceEquals(x.DataContext, vm.CurrentPage));
                    if (scroll is not null && scroll.ScrollableHeight > 0) { scroll.ScrollToEnd(); await Capture(window, Path.Combine(output!, key + "-bottom.png")); scroll.ScrollToHome(); }
                }
                vm.Navigate("home"); window.Width = 1080; window.Height = 720; await Capture(window, Path.Combine(output!, "home-compact.png"));
                window.Width = 1360; window.Height = 920; ApplyTheme(false); await Capture(window, Path.Combine(output!, "home-dark.png"));
                foreach (var key in new[] { "master", "plc", "mqtt", "netconfig", "quick" }) { vm.Navigate(key); await Capture(window, Path.Combine(output!, key + "-dark.png")); }
                ApplyTheme(true);
                await vm.Close(false);
                await WindowCloseSmoke.Run(output!);
                await SystemSettingsSmoke.Run(output!);
                PresentationTraceSources.DataBindingSource.Flush();
                File.WriteAllText(Path.Combine(output!, "bindings.log"), trace!.ToString());
                File.WriteAllText(Path.Combine(output!, "result.txt"), $"UI smoke passed: simulator, master reads/writes, polling start/stop, generated-frame send/receive, periodic send stop, log filtering.\n{Directory.GetFiles(output!, "*.png").Length} screenshots rendered, including page bottoms, compact window and light theme.\n" + (trace.ToString().Length == 0 ? "No binding errors." : "Inspect bindings.log."));
                Shutdown(trace.ToString().Length == 0 ? 0 : 2);
            }
        }
        catch (Exception ex)
        {
            if (verification) File.WriteAllText(Path.Combine(output!, "failure.txt"), ex.ToString());
            else { WriteCrash(ex); MessageBox.Show(ex.Message, "设备联调工具 · 启动失败", MessageBoxButton.OK, MessageBoxImage.Error); }
            Shutdown(1);
        }
    }
    protected override void OnExit(ExitEventArgs e)
    {
        _desktop?.Dispose(); _instance?.Dispose();
        base.OnExit(e);
    }
    internal static async Task Capture(Window window, string path)
    {
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        window.UpdateLayout(); await Task.Delay(120);
        if (Descendants<TextBlock>(window).Any(x => x.Text.StartsWith("LinkLab.App."))) throw new IOException("UI smoke: a control is displaying a type name instead of user-facing text");
        var root = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }
    internal static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T found) yield return found;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
    private static void WriteCrash(Exception ex)
    {
        try { var directory = Path.GetDirectoryName(Preferences.FilePath)!; Directory.CreateDirectory(directory); File.AppendAllText(Path.Combine(directory, "crash.log"), $"{DateTime.Now:O}\n{ex}\n", Encoding.UTF8); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}
