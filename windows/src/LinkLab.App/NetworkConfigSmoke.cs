using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using LinkLab.Core;

namespace LinkLab.App;

internal static class NetworkConfigSmoke
{
    private static void Check(bool condition, string message) { if (!condition) throw new IOException("Network configuration smoke: " + message); }
    private static NetworkDeviceSnapshot Device()
    {
        var frame = ZlanConfigurationProtocol.Query(false); frame[2] = 1; var p = frame.AsSpan(3);
        new byte[] { 192, 168, 1, 200 }.CopyTo(p); new byte[] { 255, 255, 255, 0 }.CopyTo(p[4..]); new byte[] { 192, 168, 1, 1 }.CopyTo(p[8..]);
        new byte[] { 192, 168, 1, 100 }.CopyTo(p[12..]); BinaryPrimitives.WriteUInt16BigEndian(p[16..], 4196); BinaryPrimitives.WriteUInt16BigEndian(p[18..], 4196);
        new byte[] { 2, 0x4C, 0x41, 0x42, 0, 1 }.CopyTo(p[31..]); p[37] = 4; Encoding.ASCII.GetBytes("DEMO-01").CopyTo(p[38..]);
        Encoding.ASCII.GetBytes("192.168.1.100").CopyTo(p[66..]); p[104] = 0x22; p[112] = 2;
        return ZlanConfigurationProtocol.ParseResponse(frame, new(IPAddress.Parse("192.168.1.200"), 1092));
    }
    private sealed class Backend : INetworkConfigurationBackend
    {
        public event Action<WireEvent>? Traffic { add { } remove { } }
        public NetworkDeviceSnapshot Snapshot { get; private set; } = Device();
        public int Reads { get; private set; }
        public int Writes { get; private set; }
        public bool Fail { get; set; }
        public TaskCompletionSource<IReadOnlyList<NetworkDeviceSnapshot>>? Pending { get; set; }
        public Task<IReadOnlyList<NetworkDeviceSnapshot>> Discover(string bindAddress, string target, bool broadcast, int timeout, CancellationToken ct)
        {
            Reads++; return Pending?.Task.WaitAsync(ct) ?? (Fail ? Task.FromException<IReadOnlyList<NetworkDeviceSnapshot>>(new IOException("模拟设备未应答")) : Task.FromResult<IReadOnlyList<NetworkDeviceSnapshot>>([Snapshot]));
        }
        public Task<NetworkDeviceSnapshot> Read(string bindAddress, NetworkDeviceSnapshot expected, int timeout, CancellationToken ct)
        { Reads++; return Fail ? Task.FromException<NetworkDeviceSnapshot>(new IOException("模拟设备未应答")) : Task.FromResult(Snapshot); }
        public Task<NetworkDeviceSnapshot> Apply(string bindAddress, NetworkDeviceSnapshot expected, NetworkDeviceSettings settings, string password, int timeout, CancellationToken ct)
        {
            Writes++; var packet = ZlanConfigurationProtocol.Write(Snapshot, settings, password); packet[2] = 1;
            Snapshot = ZlanConfigurationProtocol.ParseResponse(packet, Snapshot.Peer); return Task.FromResult(Snapshot);
        }
    }
    internal static async Task Run(ShellViewModel shell, Window window, string output)
    {
        var backend = new Backend(); var model = new NetworkConfigVm(shell.Hub, backend);
        Check(backend.Reads == 0 && backend.Writes == 0 && model.Devices.Count == 0 && !model.CanApply, "Construction sent packets or enabled writing without discovery.");
        await model.Search(true);
        Check(backend.Reads == 1 && model.Devices.Count == 1 && model.Selected?.Identity == backend.Snapshot.Identity && model.CanEdit, "Discovery selection and parameter loading failed.");
        model.LocalPort = "502"; model.Baud = 115200; model.Parity = 1; model.Review();
        Check(model.CanApply && model.Preview.Contains("02:4C:41:42:00:01") && model.Preview.Contains("当前 IP 192.168.1.200") && model.Preview.Contains("502"), "Review omitted device identity or changed parameters.");
        model.Mask = "255.255.0.0"; Check(!model.CanApply && model.Preview.Length == 0, "Changing a reviewed field retained stale approval.");
        model.Review(); var requested = model.Settings(); await model.ApplyReviewed(model.Selected!, requested);
        Check(backend.Writes == 1 && model.Selected?.Settings == requested && model.Status.Contains("写入成功"), "Confirmed backend result did not refresh the selected device.");
        model.Password = "test-only-password"; var preferences = new Preferences(); preferences.Transfer("netconfig", model, true);
        Check(!preferences.Values.Keys.Any(key => key.Contains("Password") || key.Contains("Parameters")), "Preferences persisted authentication or device packet contents.");
        model.Password = ""; model.Address = "192.168.1.255"; model.Mask = "255.255.255.0";
        bool rejected = false; try { model.Review(); } catch (ArgumentException) { rejected = true; }
        Check(rejected && backend.Writes == 1, "Invalid static broadcast address was accepted or sent.");
        backend.Fail = true; model.QueryCommand.Execute(null); while (!model.QueryCommand.CanExecute(null)) await Task.Delay(10);
        Check(model.HasError && model.Status.Contains("模拟设备未应答") && !model.Busy, "Failure was hidden or controls remained busy.");
        await model.Close(); await model.Close();
        var pendingBackend = new Backend { Pending = new(TaskCreationOptions.RunContinuationsAsynchronously) }; var pending = new NetworkConfigVm(shell.Hub, pendingBackend);
        pending.SearchCommand.Execute(null); Check(pending.Busy, "Pending discovery did not disable controls.");
        await pending.Close(); Check(!pending.Busy && pendingBackend.Writes == 0, "Closing a pending read failed or wrote a device.");

        // Render the production template with a clearly synthetic device. The real
        // backend is never invoked: no LAN broadcast or device write occurs here.
        var page = shell.NetworkConfig; page.Devices.Clear(); page.Devices.Add(Device()); page.Selected = page.Devices[0]; page.LocalPort = "502"; page.Baud = 115200; page.Review();
        shell.Hub.Notice = "网口参数预览 · 尚未写入设备";
        shell.Navigate("netconfig"); await App.Capture(window, Path.Combine(output, "network-configuration.png"));
        Check(App.Descendants<ComboBox>(window).Any(combo => combo.Name == "NetworkConfigInterfaces"), "Production network interface dropdown is missing.");
        Check(App.Descendants<DataGrid>(window).Any(grid => grid.Name == "NetworkConfigDevices"), "Production device list is missing.");
        var scroll = App.Descendants<ScrollViewer>(window).First(view => view.Name == "NetworkConfigScroll");
        var settingsCard = App.Descendants<Border>(window).Single(border => border.Name == "NetworkConfigSettingsCard");
        scroll.ScrollToVerticalOffset(scroll.VerticalOffset + settingsCard.TranslatePoint(new Point(), scroll).Y);
        await App.Capture(window, Path.Combine(output, "network-configuration-settings.png")); scroll.ScrollToTop();
        double width = window.Width, height = window.Height;
        window.Width = 1080; window.Height = 720;
        await App.Capture(window, Path.Combine(output, "network-configuration-compact.png"));
        scroll.ScrollToBottom();
        await App.Capture(window, Path.Combine(output, "network-configuration-review.png"));
        window.Width = width; window.Height = height; scroll.ScrollToTop();
        page.Devices.Clear(); page.Selected = null; shell.Navigate("home"); shell.Hub.Notice = "准备就绪 · 所有通信在本机执行";
        File.WriteAllText(Path.Combine(output, "network-configuration-result.txt"), "PASS: no unsolicited traffic; fake discovery and selection; parameter loading, identity-aware review, approval invalidation, apply result routing, secret-free preferences, invalid-address rejection, visible errors, busy state and repeated/active-read close. Production module template rendered at normal and compact sizes, with scrollable review. No LAN broadcasts, physical device writes or adapter changes were performed. Core tests separately exercise the real UDP transport against a loopback mock device. Physical ZLAN hardware validation is pending.\n");
    }
}
