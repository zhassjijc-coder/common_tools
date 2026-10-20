using System.Net;
using System.Net.Sockets;
using LinkLab.Core;

internal static class PlcTests
{
    private static void Check(bool condition, string message = "PLC assertion failed") { if (!condition) throw new Exception(message); }
    private static void Bytes(byte[] expected, byte[] actual) => Check(expected.SequenceEqual(actual), $"Expected {ByteTools.Hex(expected)}, got {ByteTools.Hex(actual)}");
    private static void Throws(Action action) { try { action(); } catch (ArgumentException) { return; } throw new Exception("Expected rejection"); }
    public static async Task Run(Func<string, Func<Task>, Task> test)
    {
        await test("PLC address syntax, widths and bounds", () =>
        {
            var a = PlcAddress.Parse(PlcProtocol.SiemensS7, "DB12.DBX14.7", PlcDataType.Bool); Check(a.Block == 12 && a.Index == 14 && a.Bit == 7);
            Check(PlcAddress.Parse(PlcProtocol.MitsubishiMc3E, "X10", PlcDataType.Bool).Index == 16);
            Throws(() => PlcAddress.Parse(PlcProtocol.OmronFinsTcp, "D0.16", PlcDataType.Bool));
            Throws(() => PlcAddress.Parse(PlcProtocol.SiemensS7, "DB1.DBW0", PlcDataType.Int32));
            Throws(() => PlcAddress.Parse(PlcProtocol.MitsubishiMc3E, "D0", PlcDataType.Bool));
            Throws(() => new PlcRequest(PlcAddress.Parse(PlcProtocol.OmronFinsTcp, "D65535", PlcDataType.Int32), PlcDataType.Int32, 1).Validate()); return Task.CompletedTask;
        });
        await test("PLC command builders match fixed wire vectors", () =>
        {
            var s7 = new PlcRequest(PlcAddress.Parse(PlcProtocol.SiemensS7, "DB1.0", PlcDataType.UInt16), PlcDataType.UInt16, 1);
            Bytes(ByteTools.ParseHex("03 00 00 1F 02 F0 80 32 01 00 00 00 01 00 0E 00 00 04 01 12 0A 10 02 00 02 00 01 84 00 00 00"), PlcWire.Request(s7, new(s7.Address.Protocol, "", 0), 1));
            var mc = new PlcRequest(PlcAddress.Parse(PlcProtocol.MitsubishiMc3E, "D100", PlcDataType.UInt16), PlcDataType.UInt16, 2);
            Bytes(ByteTools.ParseHex("50 00 00 FF FF 03 00 0C 00 0A 00 01 04 00 00 64 00 00 A8 02 00"), PlcWire.Request(mc, new(mc.Address.Protocol, "", 0), 1));
            var fins = new PlcRequest(PlcAddress.Parse(PlcProtocol.OmronFinsTcp, "D100", PlcDataType.UInt16), PlcDataType.UInt16, 2);
            Bytes(ByteTools.ParseHex("46 49 4E 53 00 00 00 1A 00 00 00 02 00 00 00 00 80 00 02 00 01 00 00 02 00 01 01 01 82 00 64 00 00 02"), PlcWire.Request(fins, new(fins.Address.Protocol, "", 0), 1, 2, 1)); return Task.CompletedTask;
        });
        await test("PLC endian conversion and bit/word aliasing", () =>
        {
            foreach (var order in Enum.GetValues<WordOrder>()) foreach (var type in new[] { PlcDataType.Int16, PlcDataType.UInt16, PlcDataType.Int32, PlcDataType.UInt32, PlcDataType.Float32 })
                Check(PlcValues.Decode(PlcValues.Encode("123", type, order), type, order) == "123");
            foreach (var p in new[] { PlcProtocol.SiemensS7, PlcProtocol.OmronFinsTcp })
            {
                var memory = new PlcMemory(p); string address = p == PlcProtocol.SiemensS7 ? "DB1.0" : "D0";
                memory.Write(PlcAddress.Parse(p, address + ".0", PlcDataType.Bool), [1]);
                Bytes(p == PlcProtocol.SiemensS7 ? [1, 0] : [0, 1], memory.Read(PlcAddress.Parse(p, address, PlcDataType.UInt16), 2));
                string snapshot = memory.Snapshot(); var other = new PlcMemory(p); other.Restore(snapshot); Check(other.Snapshot() == snapshot);
                Throws(() => memory.Write(PlcAddress.Parse(p, address + ".0", PlcDataType.Bool), [2]));
            }
            return Task.CompletedTask;
        });
        foreach (var protocol in Enum.GetValues<PlcProtocol>())
        {
            await test($"PLC {protocol} explicit connect negotiates without memory access", async () =>
            {
                var memory = new PlcMemory(protocol); string before = memory.Snapshot();
                await using var server = new PlcSimulator(memory, "127.0.0.1", 0); await server.StartAsync();
                await using var client = new PlcConnection(new(protocol, "127.0.0.1", server.BoundPort));
                var sent = new List<byte[]>(); client.Traffic += e => { if (e.Direction == "TX") sent.Add(e.Data); };
                Check(!client.IsConnected); await client.ConnectAsync(1500); Check(client.IsConnected);
                int expected = protocol switch { PlcProtocol.SiemensS7 => 2, PlcProtocol.OmronFinsTcp => 1, _ => 0 };
                Check(sent.Count == expected, "Connect must send only protocol handshakes");
                if (protocol == PlcProtocol.SiemensS7) Check(sent[0][5] == 0xE0 && sent[1][17] == 0xF0);
                if (protocol == PlcProtocol.OmronFinsTcp) Check(PlcWire.U32(sent[0], 8) == 0);
                await client.ConnectAsync(1500); Check(sent.Count == expected, "Repeated connect must reuse negotiated socket");
                Check(memory.Snapshot() == before, "Connect must leave PLC memory untouched");
                await client.DisposeAsync(); Check(!client.IsConnected);
                try { await client.ConnectAsync(1500); throw new Exception("Disposed connection reopened"); } catch (ObjectDisposedException) { }
            });
            await test($"PLC {protocol} real TCP read/write arrays and bits", async () =>
            {
                var memory = new PlcMemory(protocol); await using var server = new PlcSimulator(memory, "127.0.0.1", 0); await server.StartAsync();
                await using var client = new PlcConnection(new(protocol, "127.0.0.1", server.BoundPort));
                string address = protocol == PlcProtocol.SiemensS7 ? "DB1.0" : "D100";
                foreach (var type in new[] { PlcDataType.UInt16, PlcDataType.Int16, PlcDataType.Int32, PlcDataType.UInt32, PlcDataType.Float32 })
                {
                    byte[] bytes = Enumerable.Range(1, 3).SelectMany(i => PlcValues.Encode((i * 123).ToString(), type, PlcValues.DefaultOrder(protocol))).ToArray();
                    var req = new PlcRequest(PlcAddress.Parse(protocol, address, type), type, 3, bytes);
                    await client.ExecuteAsync(req, 1500); Bytes(bytes, await client.ExecuteAsync(req with { Data = null }, 1500));
                }
                string bitAddress = protocol switch { PlcProtocol.SiemensS7 => "M100.7", PlcProtocol.MitsubishiMc3E => "M100", _ => "CIO100.15" };
                byte[] bits = protocol == PlcProtocol.SiemensS7 ? [1] : [1, 0, 1, 1, 0];
                var bitRequest = new PlcRequest(PlcAddress.Parse(protocol, bitAddress, PlcDataType.Bool), PlcDataType.Bool, bits.Length, bits);
                await client.ExecuteAsync(bitRequest, 1500); Bytes(bits, await client.ExecuteAsync(bitRequest with { Data = null }, 1500));
            });
            await test($"PLC {protocol} multi-client and port release", async () =>
            {
                int port; var memory = new PlcMemory(protocol);
                await using (var server = new PlcSimulator(memory, "127.0.0.1", 0))
                {
                    await server.StartAsync(); port = server.BoundPort;
                    await Task.WhenAll(Enumerable.Range(0, 8).Select(async i =>
                    {
                        await using var client = new PlcConnection(new(protocol, "127.0.0.1", port));
                        string addr = protocol == PlcProtocol.SiemensS7 ? $"DB1.{i * 2}" : $"D{i}";
                        var req = new PlcRequest(PlcAddress.Parse(protocol, addr, PlcDataType.UInt16), PlcDataType.UInt16, 1, [0, (byte)i]);
                        await client.ExecuteAsync(req, 3000); Bytes(req.Data!, await client.ExecuteAsync(req with { Data = null }, 3000));
                    }));
                }
                var listener = new TcpListener(IPAddress.Loopback, port); listener.Start(); listener.Stop();
            });
        }
        await test("PLC explicit connect reports refused port and can recover", async () =>
        {
            var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start(); int port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
            await using var client = new PlcConnection(new(PlcProtocol.MitsubishiMc3E, "127.0.0.1", port));
            // Windows may delay ECONNREFUSED beyond the configured timeout; both must be an explicit failure.
            try { await client.ConnectAsync(1500); throw new Exception("Expected refused port"); } catch (Exception ex) when (ex is SocketException or TimeoutException) { }
            Check(!client.IsConnected);
            await using var server = new PlcSimulator(new(PlcProtocol.MitsubishiMc3E), "127.0.0.1", port); await server.StartAsync();
            await client.ConnectAsync(1500); Check(client.IsConnected, "Failed connect must release socket for retry");
        });
        foreach (var protocol in new[] { PlcProtocol.SiemensS7, PlcProtocol.OmronFinsTcp })
        {
            await test($"PLC {protocol} explicit connect rejects silent and invalid handshake", async () =>
            {
                var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                try
                {
                    await using var client = new PlcConnection(new(protocol, "127.0.0.1", port));
                    var connecting = client.ConnectAsync(200); using var peer = await listener.AcceptTcpClientAsync();
                    _ = await PlcWire.ReadFrame(peer.GetStream(), protocol, default);
                    Check(!client.IsConnected, "Accepted TCP alone must not mark S7/FINS connected");
                    try { await connecting; throw new Exception("Expected handshake timeout"); } catch (TimeoutException ex) { Check(ex.Message.Contains("握手")); }
                    Check(!client.IsConnected); Check(await peer.GetStream().ReadAsync(new byte[1]).AsTask().WaitAsync(TimeSpan.FromSeconds(1)) == 0, "Timed-out handshake socket leaked");
                    for (int stage = 0; stage < (protocol == PlcProtocol.SiemensS7 ? 2 : 1); stage++)
                    {
                        connecting = client.ConnectAsync(1500); using var badPeer = await listener.AcceptTcpClientAsync(); var stream = badPeer.GetStream();
                        var session = new PlcServerSession(new(protocol));
                        var request = await PlcWire.ReadFrame(stream, protocol, default);
                        if (stage == 1) { await stream.WriteAsync(session.Process(request)); request = await PlcWire.ReadFrame(stream, protocol, default); }
                        var reply = session.Process(request);
                        if (protocol == PlcProtocol.SiemensS7) { if (stage == 0) reply[5] = 0xE0; else reply[19] = 0; }
                        else PlcWire.Put32(reply, 16, 0);
                        await stream.WriteAsync(reply);
                        try { await connecting; throw new Exception("Expected invalid handshake"); } catch (IOException) { }
                        Check(!client.IsConnected); Check(await stream.ReadAsync(new byte[1]).AsTask().WaitAsync(TimeSpan.FromSeconds(1)) == 0, "Rejected handshake socket leaked");
                    }
                }
                finally { listener.Stop(); }
            });
            await test($"PLC {protocol} connecting cancellation and disposal release peer", async () =>
            {
                var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                try
                {
                    foreach (bool dispose in new[] { false, true })
                    {
                        await using var client = new PlcConnection(new(protocol, "127.0.0.1", port)); using var stop = new CancellationTokenSource();
                        var connecting = client.ConnectAsync(60000, stop.Token); using var peer = await listener.AcceptTcpClientAsync();
                        _ = await PlcWire.ReadFrame(peer.GetStream(), protocol, default);
                        if (dispose) await client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1)); else stop.Cancel();
                        try { await connecting.WaitAsync(TimeSpan.FromSeconds(1)); throw new Exception("Expected canceled connection"); } catch (OperationCanceledException) { }
                        Check(!client.IsConnected); Check(await peer.GetStream().ReadAsync(new byte[1]).AsTask().WaitAsync(TimeSpan.FromSeconds(1)) == 0, "Canceled connect socket leaked");
                    }
                }
                finally { listener.Stop(); }
            });
        }
        await test("PLC MC TCP split/coalesced frames and rejection leaves memory unchanged", async () =>
        {
            var memory = new PlcMemory(PlcProtocol.MitsubishiMc3E); await using var server = new PlcSimulator(memory, "127.0.0.1", 0); await server.StartAsync();
            using var client = new TcpClient(); await client.ConnectAsync("127.0.0.1", server.BoundPort); var stream = client.GetStream();
            byte[] read = ByteTools.ParseHex("50 00 00 FF FF 03 00 0C 00 0A 00 01 04 00 00 64 00 00 A8 02 00");
            await stream.WriteAsync(read.AsMemory(0, 5)); await stream.WriteAsync(read.AsMemory(5));
            Bytes(ByteTools.ParseHex("D0 00 00 FF FF 03 00 06 00 00 00 00 00 00 00"), await PlcWire.ReadFrame(stream, memory.Protocol, default));
            await stream.WriteAsync(read.Concat(read).ToArray()); _ = await PlcWire.ReadFrame(stream, memory.Protocol, default); _ = await PlcWire.ReadFrame(stream, memory.Protocol, default);
            byte[] bad = ByteTools.ParseHex("50 00 00 FF FF 03 00 0E 00 0A 00 01 14 00 00 FF FF 00 A8 02 00 01 02");
            await stream.WriteAsync(bad); Check(PlcWire.Le(await PlcWire.ReadFrame(stream, memory.Protocol, default), 9) != 0);
            Bytes([0, 0], memory.Read(PlcAddress.Parse(memory.Protocol, "D65535", PlcDataType.UInt16), 2));
        });
        await test("PLC cancellation, timeout and corrupt reply validation", async () =>
        {
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            try
            {
                var accept = listener.AcceptTcpClientAsync(); await using var client = new PlcConnection(new(PlcProtocol.MitsubishiMc3E, "127.0.0.1", port));
                var req = new PlcRequest(PlcAddress.Parse(PlcProtocol.MitsubishiMc3E, "D0", PlcDataType.UInt16), PlcDataType.UInt16, 1);
                using var stop = new CancellationTokenSource(100);
                try { await client.ExecuteAsync(req, 2000, stop.Token); throw new Exception("Expected cancellation"); } catch (OperationCanceledException) { }
                using var peer = await accept;
                var next = listener.AcceptTcpClientAsync(); try { await client.ExecuteAsync(req, 100); throw new Exception("Expected timeout"); } catch (TimeoutException) { } using var peer2 = await next;
                var frame = PlcWire.Request(req, new(req.Address.Protocol, "", 0), 1);
                try { _ = PlcWire.ParseResponse(req, frame, ByteTools.ParseHex("D0 00 00 FE FF 03 00 04 00 00 00 00 00")); throw new Exception("Expected invalid route"); } catch (IOException) { }
            }
            finally { listener.Stop(); }
        });
    }
}
