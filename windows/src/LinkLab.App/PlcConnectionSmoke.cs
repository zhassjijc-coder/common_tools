using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using LinkLab.Core;

namespace LinkLab.App;

/// <summary>Isolated VM/binding integration check. Uses only loopback and never loads preferences or captures pixels.</summary>
internal static class PlcConnectionSmoke
{
    private static void Check(bool condition, string message) { if (!condition) throw new IOException("PLC connection smoke: " + message); }
    private static int FreePort() { var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port; }
    private static async Task Until(Func<bool> condition)
    {
        for (int i = 0; i < 150 && !condition(); i++) await Task.Delay(20);
        Check(condition(), "operation did not finish within 3 seconds");
        await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    }
    internal static async Task Run(string output)
    {
        var report = new List<string>(); var hub = new Hub(); var vm = new PlcVm(hub);
        var content = new ContentControl { Content = vm };
        var window = new Window { Content = content, Width = 1200, Height = 850, Left = -16000, Top = 0, ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual };
        window.Show(); await Until(() => App.Descendants<Button>(window).Any(x => Equals(x.Content, "连接 PLC")));
        var button = App.Descendants<Button>(window).Single(x => Equals(x.Content, "连接 PLC"));
        var status = App.Descendants<TextBlock>(window).Single(x => BindingOperations.GetBindingExpression(x, TextBlock.TextProperty)?.ParentBinding.Path.Path == nameof(PlcVm.Status));
        Check(ReferenceEquals(button.Command, vm.ConnectCommand) && button.IsEnabled && !vm.IsConnected, "initial connect-button binding");
        try
        {
            foreach (var protocol in Enum.GetValues<PlcProtocol>())
            {
                await vm.Close(); vm.ProtocolIndex = (int)protocol;
                var memory = new PlcMemory(protocol); string snapshot = memory.Snapshot();
                await using var server = new PlcSimulator(memory, "127.0.0.1", 0); await server.StartAsync();
                vm.Host = "127.0.0.1"; vm.Port = server.BoundPort.ToString(); vm.Address = "invalid-unused-address"; vm.Count = "invalid-unused-count";
                button.Command.Execute(null); await Until(() => vm.CanConfigure && vm.ConnectCommand.CanExecute(null));
                Check(vm.IsConnected && !button.IsEnabled && !vm.HasError && vm.Status.Contains("已连接"), "connection must perform negotiation before success");
                Check(protocol != PlcProtocol.MitsubishiMc3E || vm.Status.Contains("MC TCP") && vm.Status.Contains("单独验证"), "MC status must describe TCP scope");
                Check(memory.Snapshot() == snapshot && vm.ClientRows.Count == 0, "explicit connect must not access memory or parse unused address/count");
                int oldPort = server.BoundPort; vm.Port = FreePort().ToString();
                Check(!vm.IsConnected && vm.CanConnect && vm.Status.Contains("参数已改变"), "changed endpoint must invalidate connection immediately");
                await Until(() => server.ClientCount == 0); vm.Port = oldPort.ToString();
                await vm.Connect(); Check(vm.IsConnected, "connect after editing endpoint");
                vm.Address = protocol == PlcProtocol.SiemensS7 ? "DB1.0" : "D100"; vm.Count = "1"; vm.Values = "321";
                await vm.Execute(true); await vm.Execute(false); Check(vm.ClientRows.Single().Value == "321", "explicitly connected read/write");
                await vm.Disconnect(); Check(!vm.IsConnected && vm.CanConnect, "disconnect state");
                await vm.Execute(false); Check(vm.IsConnected, "lazy connect remains supported");
                vm.Interval = "100"; vm.PollCommand.Execute(null); await Task.Delay(150); await vm.Disconnect();
                Check(vm.CanConfigure && !vm.IsConnected, "disconnect must cancel monitoring");
                report.Add($"PASS {protocol}: bound connect button, real negotiation, no memory side effects, endpoint invalidation/reconnect, explicit and lazy read/write, polling/disconnect.");
            }
            await vm.Close(); vm.ProtocolIndex = 1; vm.Port = FreePort().ToString(); vm.Timeout = "500"; hub.Clear();
            button.Command.Execute(null); await Until(() => vm.CanConfigure && vm.ConnectCommand.CanExecute(null)); hub.Flush();
            Check(!vm.IsConnected && vm.HasError && button.IsEnabled && status.Text.Contains("连接失败") && hub.Logs.Any(x => x.Direction == "ERR"), "refused port must report local and Hub error and restore button");
            report.Add("PASS refused port: visible error, Hub error record, no false connected state, retry enabled.");
            foreach (var protocol in new[] { PlcProtocol.SiemensS7, PlcProtocol.OmronFinsTcp })
            {
                await vm.Close(); vm.ProtocolIndex = (int)protocol;
                var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); vm.Port = ((IPEndPoint)listener.LocalEndpoint).Port.ToString();
                try
                {
                    foreach (bool invalid in new[] { false, true })
                    {
                        vm.Timeout = "200"; hub.Clear(); button.Command.Execute(null);
                        using var peer = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(1)); var stream = peer.GetStream();
                        var frame = await PlcWire.ReadFrame(stream, protocol, default);
                        Check(!vm.IsConnected && !vm.CanConnect, "TCP acceptance without handshake must remain pending");
                        if (invalid)
                        {
                            var reply = new PlcServerSession(new(protocol)).Process(frame);
                            if (protocol == PlcProtocol.SiemensS7) reply[5] = 0xE0; else PlcWire.Put32(reply, 16, 0);
                            await stream.WriteAsync(reply);
                        }
                        await Until(() => vm.CanConfigure && vm.ConnectCommand.CanExecute(null)); hub.Flush();
                        Check(!vm.IsConnected && vm.HasError && button.IsEnabled && status.Text.Contains("连接失败") && hub.Logs.Any(x => x.Direction == "ERR"), "bad/silent handshake must be visibly rejected");
                        Check(await stream.ReadAsync(new byte[1]).AsTask().WaitAsync(TimeSpan.FromSeconds(1)) == 0, "failed handshake leaked peer socket");
                    }
                    foreach (bool close in new[] { false, true })
                    {
                        vm.Timeout = "60000"; button.Command.Execute(null);
                        using var peer = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(1));
                        _ = await PlcWire.ReadFrame(peer.GetStream(), protocol, default);
                        if (close) await vm.Close().WaitAsync(TimeSpan.FromSeconds(1)); else vm.Rack = vm.Rack == "0" ? "1" : "0";
                        await Until(() => vm.CanConfigure && vm.ConnectCommand.CanExecute(null));
                        Check(!vm.IsConnected && !vm.HasError, "canceled obsolete connect must not show success/error");
                        Check(await peer.GetStream().ReadAsync(new byte[1]).AsTask().WaitAsync(TimeSpan.FromSeconds(1)) == 0, "close or parameter change leaked handshake socket");
                    }
                }
                finally { listener.Stop(); }
                report.Add($"PASS {protocol}: no handshake response, invalid handshake, parameter change during connect, closing during connect; failure/cancellation release sockets.");
            }
            vm.Host = "127.0.0.1"; vm.Timeout = "2000"; vm.ServerPort = FreePort().ToString(); await vm.Loopback();
            Check(vm.IsConnected && vm.Running && vm.ClientRows.Count == 1, "local loopback must still work after all failures");
            report.Add("PASS local simulator/loopback recovery after failures.");
            File.WriteAllLines(Path.Combine(output, "plc-connect-result.txt"), report);
        }
        finally { await vm.Close(); window.Close(); }
    }
}
