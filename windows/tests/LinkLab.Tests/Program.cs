using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using LinkLab.Core;

if (args.Length == 2 && args[0] == "--broker") { await MqttTests.RunBroker(int.Parse(args[1])); return 0; }

int passed = 0, failed = 0;
async Task Test(string name, Func<Task> action)
{
    if (args.Length == 2 && args[0] == "--filter" && !name.Contains(args[1], StringComparison.OrdinalIgnoreCase)) return;
    try { await action().WaitAsync(TimeSpan.FromSeconds(10)); passed++; Console.WriteLine($"PASS {name}"); }
    catch (Exception e) { failed++; Console.WriteLine($"FAIL {name}: {e}"); }
}
void Check(bool value, string message = "Assertion failed") { if (!value) throw new Exception(message); }
void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}"); }
void Bytes(byte[] a, byte[] b) { if (!a.SequenceEqual(b)) throw new Exception($"Expected {ByteTools.Hex(a)}, got {ByteTools.Hex(b)}"); }
void Throws<T>(Action a) where T : Exception { try { a(); } catch (T) { return; } throw new Exception($"Expected {typeof(T).Name}"); }
async Task ThrowsAsync<T>(Func<Task> a) where T : Exception { try { await a(); } catch (T) { return; } throw new Exception($"Expected {typeof(T).Name}"); }
Task Sync(Action a) { a(); return Task.CompletedTask; }
ModbusRequest Read(byte fc = 3, ushort addr = 0, ushort quantity = 10) => new(1, fc, addr, quantity, []);

await Test("CRC16-Modbus canonical check vector", () => Sync(() => Equal((ushort)0x4B37, ByteTools.Crc16(Encoding.ASCII.GetBytes("123456789")))));
await Test("CRC32 canonical check vector", () => Sync(() => Equal(0xCBF43926u, ByteTools.Crc32(Encoding.ASCII.GetBytes("123456789")))));
await Test("HEX input formats and round trip", () => Sync(() => Bytes([1, 3, 0, 255], ByteTools.ParseHex("0x01,03-00:FF"))));
await Test("Malformed HEX rejected", () => Sync(() => { Throws<FormatException>(() => ByteTools.ParseHex("12Q3")); Throws<FormatException>(() => ByteTools.ParseHex("123")); }));
await Test("Checksum append order and invalid CRC", () => Sync(() => { var b = Read().Rtu(); Equal("01 03 00 00 00 0A C5 CD", ByteTools.Hex(b)); Check(ByteTools.ValidRtu(b)); b[^1] ^= 1; Check(!ByteTools.ValidRtu(b)); }));
await Test("SUM XOR LRC overflow", () => Sync(() => { Equal((byte)0xFE, ByteTools.AppendChecksum([255, 255], ChecksumKind.Sum8)[^1]); Equal((byte)0, ByteTools.AppendChecksum([255, 255], ChecksumKind.Xor8)[^1]); Equal((byte)2, ByteTools.AppendChecksum([255, 255], ChecksumKind.Lrc)[^1]); }));
await Test("Address references and bounds", () => Sync(() => { Equal((ushort)0, ModbusProtocol.Address(40001, true, 3)); Equal((ushort)0, ModbusProtocol.Address(30001, true, 4)); Equal((ushort)0, ModbusProtocol.Address(10001, true, 2)); Equal((ushort)0, ModbusProtocol.Address(1, true, 1)); Throws<ArgumentException>(() => ModbusProtocol.Address(40000, true, 3)); Throws<ArgumentException>(() => Read(3, 65535, 2).Pdu()); }));
await Test("Function quantity boundaries", () => Sync(() => { _ = Read(3, 0, 125).Pdu(); Throws<ArgumentException>(() => Read(3, 0, 126).Pdu()); _ = Read(1, 0, 2000).Pdu(); Throws<ArgumentException>(() => Read(1, 0, 2001).Pdu()); Throws<ArgumentException>(() => Read(3, 0, 0).Pdu()); }));
await Test("Invalid write payload rejected", () => Sync(() => { Throws<ArgumentException>(() => new ModbusRequest(1, 16, 0, 2, [1]).Pdu()); Throws<ArgumentException>(() => new ModbusRequest(1, 5, 0, 1, [2]).Pdu()); Throws<ArgumentException>(() => new ModbusRequest(1, 16, 0, 124, new ushort[124]).Pdu()); }));
await Test("TCP MBAP golden request", () => Sync(() => Equal("00 01 00 00 00 06 01 03 00 00 00 0A", ByteTools.Hex(Read().Tcp()))));
await Test("Response byte count, function and write echo validated", () => Sync(() => { Throws<IOException>(() => ModbusProtocol.ParseResponse(Read(3, 0, 1), [3, 1, 42])); Throws<IOException>(() => ModbusProtocol.ParseResponse(Read(3, 0, 1), [4, 2, 0, 42])); Throws<IOException>(() => ModbusProtocol.ParseResponse(new(1, 6, 0, 1, [3]), [6, 0, 0, 0, 4])); Throws<ModbusException>(() => ModbusProtocol.ParseResponse(Read(), [0x83, 2])); }));
await Test("Signed and float endian interpretations", () => Sync(() => { Equal("-1", ByteTools.Decode([65535], 0, "Int16", WordOrder.ABCD)); Equal("1", ByteTools.Decode([0x3F80, 0], 0, "Float32", WordOrder.ABCD)); Equal("1", ByteTools.Decode([0, 0x3F80], 0, "Float32", WordOrder.CDAB)); Equal("1", ByteTools.Decode([0x803F, 0], 0, "Float32", WordOrder.BADC)); Equal("1", ByteTools.Decode([0, 0x803F], 0, "Float32", WordOrder.DCBA)); }));
await Test("Simulator register writes, reads and independent input area", () => Sync(() => { var m = new ModbusMemory(); var w = new ModbusRequest(1, 16, 12, 3, [1, 65535, 42]); _ = ModbusProtocol.ParseResponse(w, m.Process(w.Pdu())); var r = Read(3, 12, 3); Check(ModbusProtocol.ParseResponse(r, m.Process(r.Pdu())).SequenceEqual(w.Values)); Equal((ushort)0, m.Get(DataArea.InputRegisters, 12)); }));
await Test("Simulator packed coil write across byte boundary", () => Sync(() => { var m = new ModbusMemory(); var w = new ModbusRequest(1, 15, 11, 10, [1, 0, 1, 1, 0, 0, 1, 1, 1, 0]); _ = m.Process(w.Pdu()); var r = Read(1, 11, 10); Check(ModbusProtocol.ParseResponse(r, m.Process(r.Pdu())).SequenceEqual(w.Values)); Equal((ushort)0, m.Get(DataArea.DiscreteInputs, 11)); }));
await Test("Simulator single coil and register writes", () => Sync(() => { var m = new ModbusMemory(); foreach (byte fc in new byte[] { 5, 6 }) { var w = new ModbusRequest(1, fc, 65535, 1, [1]); Bytes(w.Pdu(), m.Process(w.Pdu())); } Equal((ushort)1, m.Get(DataArea.Coils, 65535)); Equal((ushort)1, m.Get(DataArea.HoldingRegisters, 65535)); }));
await Test("Simulator malformed writes leave memory unchanged", () => Sync(() => { var m = new ModbusMemory(); m.Set(DataArea.HoldingRegisters, 0, 99); Bytes([0x90, 3], m.Process([16, 0, 0, 0, 2, 4, 0, 1])); Equal((ushort)99, m.Get(DataArea.HoldingRegisters, 0)); Bytes([0x85, 3], m.Process([5, 0, 0, 0, 1])); Bytes([0x83, 2], m.Process([3, 255, 255, 0, 2])); Bytes([0xFF, 1], m.Process([127])); }));
await Test("Frame description and invalid MBAP", () => Sync(() => { Check(ModbusProtocol.Describe(Read().Rtu(), false).Contains("通过")); Check(ModbusProtocol.Describe(Read().Tcp(34), true).Contains("34")); Throws<FormatException>(() => ModbusProtocol.Describe([0, 1, 1, 0, 0, 2, 1, 3], true)); }));

await Test("TCP master / simulator all eight functions over sockets", async () =>
{
    var m = new ModbusMemory(); m.Set(DataArea.InputRegisters, 7, 2468); m.Set(DataArea.DiscreteInputs, 7, 1);
    await using var sim = new ModbusTcpSimulator(m, 1, "127.0.0.1", 0); await sim.StartAsync();
    await using var client = new ModbusTcpConnection("127.0.0.1", sim.BoundPort);
    foreach (var w in new ModbusRequest[] { new(1, 5, 7, 1, [1]), new(1, 6, 7, 1, [1234]), new(1, 15, 20, 10, [1, 1, 1, 0, 0, 1, 0, 1, 1, 1]), new(1, 16, 20, 3, [42, 43, 44]) }) Check((await client.ExecuteAsync(w, 1000)).SequenceEqual(w.Values));
    Equal((ushort)1, (await client.ExecuteAsync(Read(1, 7, 1), 1000))[0]);
    Equal((ushort)1, (await client.ExecuteAsync(Read(2, 7, 1), 1000))[0]);
    Equal((ushort)1234, (await client.ExecuteAsync(Read(3, 7, 1), 1000))[0]);
    Equal((ushort)2468, (await client.ExecuteAsync(Read(4, 7, 1), 1000))[0]);
    Check((await client.ExecuteAsync(Read(3, 20, 3), 1000)).SequenceEqual(new ushort[] { 42, 43, 44 }));
});
await Test("TCP framing: fragmented header and coalesced requests", async () =>
{
    await using var sim = new ModbusTcpSimulator(new(), 1, "127.0.0.1", 0); await sim.StartAsync();
    using var client = new TcpClient(); await client.ConnectAsync("127.0.0.1", sim.BoundPort); var s = client.GetStream();
    var a = Read().Tcp(3); var b = Read().Tcp(4);
    await s.WriteAsync(a.AsMemory(0, 2)); await Task.Delay(10); await s.WriteAsync(a.AsMemory(2)); await s.WriteAsync(b);
    var r1 = await ModbusIo.ReadTcpAsync(s, default); var r2 = await ModbusIo.ReadTcpAsync(s, default);
    Equal((byte)3, r1[1]); Equal((byte)4, r2[1]);
});
await Test("TCP simulator protocol exceptions and malformed length closes socket", async () =>
{
    await using var sim = new ModbusTcpSimulator(new(), 1, "127.0.0.1", 0); await sim.StartAsync();
    using var client = new TcpClient(); await client.ConnectAsync("127.0.0.1", sim.BoundPort); var s = client.GetStream();
    await s.WriteAsync(ModbusProtocol.TcpFrame(7, 1, [3, 0, 0, 0, 0])); var response = await ModbusIo.ReadTcpAsync(s, default); Bytes([0x83, 3], response[7..]);
    await s.WriteAsync(new byte[] { 0, 1, 0, 0, 255, 255 }); Equal(0, await s.ReadAsync(new byte[1]));
});
await Test("TCP master timeout and reconnect", async () =>
{
    await using var sim = new ModbusTcpSimulator(new(), 1, "127.0.0.1", 0); await sim.StartAsync();
    await using var client = new ModbusTcpConnection("127.0.0.1", sim.BoundPort);
    await ThrowsAsync<TimeoutException>(() => client.ExecuteAsync(Read() with { Unit = 2 }, 120));
    Equal(10, (await client.ExecuteAsync(Read(), 1000)).Length);
});
await Test("TCP master validates transaction ID", async () =>
{
    var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port;
    var fake = Task.Run(async () => { using var client = await listener.AcceptTcpClientAsync(); var s = client.GetStream(); _ = await ModbusIo.ReadTcpAsync(s, default); await s.WriteAsync(ModbusProtocol.TcpFrame(999, 1, [3, 2, 0, 1])); });
    try { await using var client = new ModbusTcpConnection("127.0.0.1", port); await ThrowsAsync<IOException>(() => client.ExecuteAsync(Read(3, 0, 1), 1000)); await fake; }
    finally { listener.Stop(); }
});
await Test("TCP multiple masters concurrent writes", async () =>
{
    var m = new ModbusMemory(); await using var sim = new ModbusTcpSimulator(m, 1, "127.0.0.1", 0); await sim.StartAsync();
    await Task.WhenAll(Enumerable.Range(0, 12).Select(async i => { await using var c = new ModbusTcpConnection("127.0.0.1", sim.BoundPort); await c.ExecuteAsync(new(1, 6, (ushort)i, 1, [(ushort)(100 + i)]), 2000); }));
    for (ushort i = 0; i < 12; i++) Equal((ushort)(100 + i), m.Get(DataArea.HoldingRegisters, i));
});
await Test("TCP cancellation releases session", async () =>
{
    await using var sim = new ModbusTcpSimulator(new(), 1, "127.0.0.1", 0); await sim.StartAsync();
    await using var c = new ModbusTcpConnection("127.0.0.1", sim.BoundPort); using var stop = new CancellationTokenSource(80);
    await ThrowsAsync<OperationCanceledException>(() => c.ExecuteAsync(Read() with { Unit = 2 }, 5000, stop.Token));
    Equal(10, (await c.ExecuteAsync(Read(), 1000)).Length);
});
await Test("Raw TCP server echoes, broadcasts and releases port", async () =>
{
    int port;
    await using (var server = new NetworkTransport(NetworkMode.TcpServer, "127.0.0.1", 1, "127.0.0.1", 0) { Echo = true })
    {
        await server.StartAsync(); port = server.BoundPort;
        using var a = new TcpClient(); using var b = new TcpClient(); await a.ConnectAsync("127.0.0.1", port); await b.ConnectAsync("127.0.0.1", port);
        for (int i = 0; i < 100 && server.Peers.Length != 2; i++) await Task.Delay(10);
        Equal(2, server.Peers.Length); await a.GetStream().WriteAsync(new byte[] { 1, 2, 3 }); byte[] echo = new byte[3]; await a.GetStream().ReadExactlyAsync(echo); Bytes([1, 2, 3], echo);
        server.Echo = false; await server.SendAsync([42]); byte[] v = new byte[1]; await a.GetStream().ReadExactlyAsync(v); Equal((byte)42, v[0]); await b.GetStream().ReadExactlyAsync(v); Equal((byte)42, v[0]);
    }
    var listener = new TcpListener(IPAddress.Loopback, port); listener.Start(); listener.Stop();
});
await Test("Raw TCP client actual send / receive", async () =>
{
    await using var server = new NetworkTransport(NetworkMode.TcpServer, "127.0.0.1", 1, "127.0.0.1", 0) { Echo = true }; await server.StartAsync();
    await using var client = new NetworkTransport(NetworkMode.TcpClient, "127.0.0.1", server.BoundPort, "127.0.0.1", 0);
    var channel = Channel.CreateUnbounded<WireEvent>(); client.Traffic += e => { if (e.Direction == "RX") channel.Writer.TryWrite(e); };
    await client.StartAsync(); await client.SendAsync([1, 3, 5, 7]); Bytes([1, 3, 5, 7], (await channel.Reader.ReadAsync()).Data);
});
await Test("UDP destination send and source-address echo", async () =>
{
    using var peer = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)); int port = ((IPEndPoint)peer.Client.LocalEndPoint!).Port;
    await using var udp = new NetworkTransport(NetworkMode.Udp, "127.0.0.1", port, "127.0.0.1", 0) { Echo = true }; await udp.StartAsync();
    await udp.SendAsync([9, 8, 7]); Bytes([9, 8, 7], (await peer.ReceiveAsync()).Buffer);
    await peer.SendAsync(new byte[] { 2, 4, 6 }, new IPEndPoint(IPAddress.Loopback, udp.BoundPort)); Bytes([2, 4, 6], (await peer.ReceiveAsync()).Buffer);
});
await Test("UDP fixed reply", async () =>
{
    using var peer = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
    await using var udp = new NetworkTransport(NetworkMode.Udp, "127.0.0.1", ((IPEndPoint)peer.Client.LocalEndPoint!).Port, "127.0.0.1", 0) { AutoReply = [0x4F, 0x4B] }; await udp.StartAsync();
    await peer.SendAsync(new byte[] { 1 }, new IPEndPoint(IPAddress.Loopback, udp.BoundPort)); Bytes([0x4F, 0x4B], (await peer.ReceiveAsync()).Buffer);
});

await PlcTests.Run(Test);
await MqttTests.Run(Test);
await VirtualSerialTests.Run(Test);
await Test("Device network configuration protocol and UDP loopback", NetworkConfigurationTests.Run);
await Test("Device network restart protocol and UDP loopback", NetworkConfigurationTests.RunRestart);
Console.WriteLine($"\n{passed} passed; {failed} failed.");
return failed == 0 ? 0 : 1;
