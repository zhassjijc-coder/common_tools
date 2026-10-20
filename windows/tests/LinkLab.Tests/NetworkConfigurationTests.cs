using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using LinkLab.Core;

internal static class NetworkConfigurationTests
{
    private static void Check(bool value, string message) { if (!value) throw new IOException(message); }
    private static void Reject<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new IOException($"Expected {typeof(T).Name}."); }
    private static async Task RejectAsync<T>(Func<Task> action) where T : Exception { try { await action(); } catch (T) { return; } throw new IOException($"Expected {typeof(T).Name}."); }
    internal static byte[] Response()
    {
        var frame = ZlanConfigurationProtocol.Query(false); frame[2] = 1; var p = frame.AsSpan(3);
        IPAddress.Loopback.GetAddressBytes().CopyTo(p); new byte[] { 255, 0, 0, 0 }.CopyTo(p[4..]);
        IPAddress.Loopback.GetAddressBytes().CopyTo(p[12..]); BinaryPrimitives.WriteUInt16BigEndian(p[16..], 4196); BinaryPrimitives.WriteUInt16BigEndian(p[18..], 4196);
        Encoding.ASCII.GetBytes("smoke-key").CopyTo(p[21..]); new byte[] { 2, 0x4C, 0x41, 0x42, 0, 1 }.CopyTo(p[31..]);
        p[37] = 4; Encoding.ASCII.GetBytes("LL-DEMO").CopyTo(p[38..]); p[48] = 1;
        p[49] = 3; BinaryPrimitives.WriteUInt16BigEndian(p[50..], 1300);
        new byte[] { 8, 8, 4, 4 }.CopyTo(p[62..]); Encoding.ASCII.GetBytes("127.0.0.1").CopyTo(p[66..]);
        p[96] = 12; p[97] = 60; p[99] = 80; p[103] = 117; p[104] = 0x22; new byte[] { 230, 90, 76, 1 }.CopyTo(p[105..]); p[112] = 2; p[114] = 0xA0;
        p[115] = 255; p[116] = 3; p[117] = 0x41; p[118] = 0x42; p[119] = 0x43;
        return frame;
    }
    internal static async Task Run()
    {
        var query = ZlanConfigurationProtocol.Query(true);
        Check(query.Length == 170 && query[0] == 0x5A && query[1] == 0x4C && query[2] == 0 && query[3..].All(value => value == 0), "Golden broadcast query differs from the official 170-byte format.");
        Check(ZlanConfigurationProtocol.Query(false)[2] == 4, "Directed read command must be 0x04.");
        var frame = Response(); var endpoint = new IPEndPoint(IPAddress.Loopback, 1092); var device = ZlanConfigurationProtocol.ParseResponse(frame, endpoint);
        Check(device.Identity == "02:4C:41:42:00:01" && device.Settings.Address == "127.0.0.1" && device.Settings.Baud == 9600 && device.Settings.Parity == 1 && NetworkConfiguration.Parities[1] == "Even", "Golden parameter offsets, DevID or parity mapping are wrong.");
        var requested = device.Settings with { Name = "LL-NEW", LocalPort = 502, Mode = 1, Baud = 115200, DataBits = 7, StopBits = 2, Parity = 2, FlowControl = 1, Destination = "device.local" };
        var write = ZlanConfigurationProtocol.Write(device, requested); var returned = write.ToArray(); returned[2] = 1;
        var readback = ZlanConfigurationProtocol.ParseResponse(returned, endpoint);
        Check(ZlanConfigurationProtocol.Matches(readback, device, requested) && write[2] == 2 && write[19] == 1 && write[20] == 246, "Write/readback failed or port is not big-endian.");
        int[] changedOffsets = [16, 17, 20, 37, 38, 39, 40, 41, 42, 43, 44, 45, 46, 47, 48, 57, 59, 66, 67, 68, 69, 70, 71, 72, 73, 74, 75, 76, 77, 78, 79, 80, 81, 82, 83, 84, 85, 86, 87, 88, 89, 90, 91, 92, 93, 94, 95, 114];
        Check(Enumerable.Range(0, 167).Where(offset => !changedOffsets.Contains(offset)).All(offset => write[offset + 3] == frame[offset + 3]), "Write overwrote unrelated original parameters.");
        Check((write[117] & 0xFE) == 0xA0 && write.AsSpan(118).SequenceEqual(frame.AsSpan(118)), "Stop-bit update overwrote reserved bits or variable extensions.");
        Check(frame.SequenceEqual(Response()), "Original parameter snapshot was mutated.");
        Reject<InvalidOperationException>(() => ZlanConfigurationProtocol.Write(device, device.Settings with { AppProtocol = 1 }));
        Reject<InvalidOperationException>(() => ZlanConfigurationProtocol.Write(device, device.Settings with { AppProtocol = 2 }));
        var capableFrame = frame.ToArray(); capableFrame[107] |= 12; var capable = ZlanConfigurationProtocol.ParseResponse(capableFrame, endpoint);
        foreach (int protocol in new[] { 1, 2 })
        {
            var transformed = ZlanConfigurationProtocol.Write(capable, capable.Settings with { AppProtocol = protocol }); transformed[2] = 1;
            Check(transformed[63] == protocol && ZlanConfigurationProtocol.ParseResponse(transformed, endpoint).Settings.AppProtocol == protocol, "Application protocol offset or capability handling is wrong.");
        }
        var existingMode = frame.ToArray(); existingMode[63] = 1; var preservedMode = ZlanConfigurationProtocol.ParseResponse(existingMode, endpoint);
        Check(ZlanConfigurationProtocol.Write(preservedMode, preservedMode.Settings)[63] == 1, "An unsupported unchanged application protocol was overwritten.");
        var multicastFrame = frame.ToArray(); multicastFrame[23] = 3; var multicast = ZlanConfigurationProtocol.ParseResponse(multicastFrame, endpoint);
        var multicastWrite = ZlanConfigurationProtocol.Write(multicast, multicast.Settings with { Baud = 19200 });
        Check(multicastWrite.AsSpan(15, 4).SequenceEqual(multicastFrame.AsSpan(15, 4)) && multicastWrite.AsSpan(69, 30).SequenceEqual(multicastFrame.AsSpan(69, 30)), "Editing multicast serial parameters overwrote unrelated unicast destination fields.");
        var protectedFrame = frame.ToArray(); protectedFrame[113] = 4; var protectedDevice = ZlanConfigurationProtocol.ParseResponse(protectedFrame, endpoint);
        Reject<ArgumentException>(() => ZlanConfigurationProtocol.Write(protectedDevice, protectedDevice.Settings));
        var protectedWrite = ZlanConfigurationProtocol.Write(protectedDevice, protectedDevice.Settings, "test-pass");
        var safe = ZlanConfigurationProtocol.Redacted(protectedWrite);
        Check(safe.AsSpan(24, 10).ToArray().All(value => value == 0) && safe.AsSpan(118).ToArray().All(value => value == 0), "Authentication or extension secrets leaked into traffic logs.");
        Check(protectedFrame.AsSpan(24, 10).SequenceEqual(frame.AsSpan(24, 10)), "Entering a password mutated the original snapshot.");
        var extended = frame.Concat(new byte[] { 0 }).ToArray(); var readonlyDevice = ZlanConfigurationProtocol.ParseResponse(extended, endpoint);
        Check(!readonlyDevice.Writable, "Unknown parameter lengths must be read-only."); Reject<InvalidOperationException>(() => ZlanConfigurationProtocol.Write(readonlyDevice, readonlyDevice.Settings));
        var channel1 = frame.ToArray(); channel1[103] = 1; var channel2 = frame.ToArray(); channel2[103] = 2;
        Check(ZlanConfigurationProtocol.ParseResponse(channel1, endpoint).Identity != ZlanConfigurationProtocol.ParseResponse(channel2, endpoint).Identity, "Multi-port channels share an unsafe identity.");
        Reject<FormatException>(() => ZlanConfigurationProtocol.ParseResponse(frame[..117], endpoint));
        var bad = frame.ToArray(); bad[2] = 2; Reject<FormatException>(() => ZlanConfigurationProtocol.ParseResponse(bad, endpoint));
        Reject<ArgumentException>(() => NetworkConfiguration.Ipv4("1.2", "IP")); Reject<ArgumentException>(() => NetworkConfiguration.Ipv4("010.0.0.1", "IP"));
        Reject<ArgumentException>(() => NetworkConfiguration.Validate(device.Settings with { Address = "192.168.1.0", Mask = "255.255.255.0" }));
        Reject<ArgumentException>(() => NetworkConfiguration.Validate(device.Settings with { Address = "192.168.1.255", Mask = "255.255.255.0" }));
        Reject<ArgumentException>(() => NetworkConfiguration.Validate(device.Settings with { Mask = "255.0.255.0" }));
        Reject<ArgumentException>(() => NetworkConfiguration.Validate(device.Settings with { Destination = "1.2" }));
        Reject<ArgumentException>(() => NetworkConfiguration.Validate(device.Settings with { Destination = new string('a', 30) }));
        var lease = device.Settings with { Dhcp = true }; var leased = device with { Settings = lease with { Address = "10.8.9.10", Mask = "255.255.0.0", Gateway = "10.8.0.1" } };
        Check(ZlanConfigurationProtocol.Matches(leased, device, lease), "DHCP confirmation incorrectly compares dynamic lease addresses.");

        await using var server = new MockDevice(); var backend = new ZlanNetworkConfigurationBackend(server.Port); var traffic = new List<WireEvent>(); backend.Traffic += traffic.Add;
        Check(server.Queries == 0 && server.Writes == 0, "Constructing the backend sent unsolicited packets.");
        var discovered = await backend.Discover("127.0.0.1", "127.0.0.1", true, 120, default);
        Check(discovered.Count == 1 && server.LastCommand == 0, "Read-only discovery did not deduplicate valid replies or ignore noise.");
        var selected = discovered[0]; var reread = await backend.Read("127.0.0.1", selected, 120, default);
        Check(reread.Identity == selected.Identity && server.LastCommand == 4, "Directed read did not preserve device identity.");
        var changed = selected.Settings with { Name = "MOCK-NEW", LocalPort = 502, Baud = 19200 };
        var confirmed = await backend.Apply("127.0.0.1", selected, changed, "", 120, default);
        Check(confirmed.Settings == changed && server.Writes == 1, "Loopback device write was not verified by an actual UDP readback.");
        server.ReplaceName("EXTERNAL");
        await RejectAsync<InvalidOperationException>(() => backend.Apply("127.0.0.1", confirmed, changed with { Name = "RACE" }, "", 120, default));
        Check(server.Writes == 1, "Concurrent parameter changes reached the write command.");
        var latest = await backend.Read("127.0.0.1", confirmed, 120, default);
        server.IgnoreWrite = true;
        await RejectAsync<TimeoutException>(() => backend.Apply("127.0.0.1", latest, latest.Settings with { Name = "NO-ACK" }, "", 120, default));
        Check(server.Writes == 2, "An unconfirmed write was automatically retried.");
        Check(traffic.All(entry => entry.Data.Length < 34 || entry.Data.AsSpan(24, 10).ToArray().All(value => value == 0)), "Backend traffic exposed authentication bytes.");
        using var cancel = new CancellationTokenSource(); cancel.Cancel(); int before = server.Queries;
        await RejectAsync<OperationCanceledException>(() => backend.Discover("127.0.0.1", "127.0.0.1", false, 120, cancel.Token));
        Check(server.Queries == before, "A cancelled operation still sent a query.");
    }

    private sealed class MockDevice : IAsyncDisposable
    {
        private readonly UdpClient _server = new(new IPEndPoint(IPAddress.Loopback, 0));
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;
        private byte[] _frame = Response();
        public int Port => ((IPEndPoint)_server.Client.LocalEndPoint!).Port;
        public int Queries { get; private set; }
        public int Writes { get; private set; }
        public byte LastCommand { get; private set; }
        public bool IgnoreWrite { get; set; }
        public MockDevice() => _loop = Loop();
        public void ReplaceName(string name) { _frame.AsSpan(41, 10).Clear(); Encoding.ASCII.GetBytes(name).CopyTo(_frame, 41); }
        private async Task Loop()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var received = await _server.ReceiveAsync(_stop.Token); var bytes = received.Buffer;
                    if (bytes.Length != 170 || bytes[0] != 0x5A || bytes[1] != 0x4C) continue;
                    LastCommand = bytes[2];
                    if (bytes[2] is 0 or 4)
                    {
                        Queries++; await _server.SendAsync(new byte[] { 1, 2, 3 }, received.RemoteEndPoint, _stop.Token);
                        await _server.SendAsync(_frame, received.RemoteEndPoint, _stop.Token); await _server.SendAsync(_frame, received.RemoteEndPoint, _stop.Token);
                    }
                    else if (bytes[2] == 2)
                    {
                        Writes++;
                        if (!IgnoreWrite && bytes.AsSpan(34, 6).SequenceEqual(_frame.AsSpan(34, 6))) { _frame = bytes.ToArray(); _frame[2] = 1; }
                    }
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (ObjectDisposedException) when (_stop.IsCancellationRequested) { }
        }
        public async ValueTask DisposeAsync() { _stop.Cancel(); _server.Dispose(); await _loop; _stop.Dispose(); }
    }
}
