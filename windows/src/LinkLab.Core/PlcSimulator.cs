using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace LinkLab.Core;

public sealed class PlcSimulator(PlcMemory memory, string bind, int port) : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<int, TcpClient> _clients = new();
    private readonly ConcurrentDictionary<int, Task> _tasks = new();
    private TcpListener? _listener;
    private Task? _accept;
    private int _next;
    public int BoundPort => (_listener?.LocalEndpoint as IPEndPoint)?.Port ?? 0;
    public int ClientCount => _clients.Count;
    public event Action<WireEvent>? Traffic;
    public Task StartAsync()
    {
        if (_listener is not null) throw new InvalidOperationException("服务器已启动。");
        var listener = new TcpListener(IPAddress.Parse(bind), port); listener.Start(); _listener = listener;
        _accept = Accept(); return Task.CompletedTask;
    }
    private async Task Accept()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener!.AcceptTcpClientAsync(_stop.Token);
                if (_clients.Count >= 64) { client.Dispose(); continue; }
                int id = Interlocked.Increment(ref _next); client.NoDelay = true; _clients[id] = client;
                Task task = Serve(id, client); _tasks[id] = task;
                _ = task.ContinueWith(_ => _tasks.TryRemove(id, out var ignored), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException) { if (!_stop.IsCancellationRequested) Record("ERR", [], "", ex.Message); }
    }
    private async Task Serve(int id, TcpClient client)
    {
        string peer = client.Client.RemoteEndPoint?.ToString() ?? ""; var session = new PlcServerSession(memory, (byte)(2 + id % 250));
        Record("INFO", [], peer, "客户端已连接");
        try
        {
            using var stream = client.GetStream();
            while (!_stop.IsCancellationRequested)
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token); idle.CancelAfter(TimeSpan.FromMinutes(5));
                var request = await PlcWire.ReadFrame(stream, memory.Protocol, idle.Token); Record("RX", request, peer);
                byte[] reply = session.Process(request); await stream.WriteAsync(reply, _stop.Token); Record("TX", reply, peer);
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException or ArgumentException)
        { if (!_stop.IsCancellationRequested && ex is not EndOfStreamException) Record("ERR", [], peer, ex.Message); }
        finally { _clients.TryRemove(id, out _); client.Dispose(); Record("INFO", [], peer, "客户端已断开"); }
    }
    private void Record(string direction, byte[] bytes, string peer, string note = "") => Traffic?.Invoke(new(DateTimeOffset.Now, $"PLC 模拟 {memory.Protocol}", direction, bytes, peer, note));
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync(); _listener?.Stop(); foreach (var client in _clients.Values) client.Dispose();
        if (_accept is not null) await _accept; await Task.WhenAll(_tasks.Values); _stop.Dispose();
    }
}

/// <summary>Protocol server subset: memory read/write, no CPU program execution.</summary>
public sealed class PlcServerSession(PlcMemory memory, byte assignedNode = 2)
{
    private int _state;
    private byte _node;
    public byte[] Process(byte[] f) => memory.Protocol switch { PlcProtocol.SiemensS7 => S7(f), PlcProtocol.MitsubishiMc3E => Mc(f), _ => Fins(f) };
    private byte[] S7(byte[] f)
    {
        if (_state == 0)
        {
            if (f.Length < 11 || f[5] != 0xE0 || f[4] != f.Length - 5) throw new IOException("需要 COTP 连接请求。");
            byte[] reply = f.ToArray(); reply[5] = 0xD0; reply[6] = f[8]; reply[7] = f[9]; reply[8] = 0; reply[9] = 1; _state = 1; return reply;
        }
        if (f.Length < 18 || f[4] != 2 || f[5] != 0xF0 || f[6] != 0x80 || f[7] != 0x32 || f[8] != 1 || f.Length != 17 + PlcWire.Be(f, 13) + PlcWire.Be(f, 15)) throw new IOException("S7 请求格式错误。");
        if (f[17] == 0xF0 && _state == 1)
        {
            if (f.Length != 25 || PlcWire.Be(f, 13) != 8 || PlcWire.Be(f, 23) < 240) return PlcWire.S7Ack(f, [], [], 0x8104);
            _state = 2; byte[] param = [0xF0, 0, 0, 1, 0, 1, 0, 0]; PlcWire.PutBe(param, 6, Math.Min(480, (int)PlcWire.Be(f, 23))); return PlcWire.S7Ack(f, param, []);
        }
        if (_state != 2 || f.Length < 31 || f[17] is not (4 or 5) || f[18] != 1 || PlcWire.Be(f, 13) != 14) return PlcWire.S7Ack(f, [], [], 0x8104);
        bool write = f[17] == 5, bit = f[22] == 1;
        try
        {
            if (f[19] != 0x12 || f[20] != 10 || f[21] != 0x10 || f[22] is not (1 or 2 or 4 or 6 or 8)) throw new ArgumentException("不支持的 S7 变量类型。");
            int count = PlcWire.Be(f, 23), length = count * (f[22] switch { 4 => 2, 6 or 8 => 4, _ => 1 });
            int address = (f[28] << 16) | (f[29] << 8) | f[30]; byte area = f[27]; ushort block = PlcWire.Be(f, 25);
            if (length is < 1 or > 400 || (bit && count != 1) || (!bit && address % 8 != 0) || (area == 0x84 ? block == 0 : area is not (0x81 or 0x82 or 0x83) || block != 0)) throw new ArgumentException("地址或数量不支持。");
            var a = new PlcAddress(memory.Protocol, area, block, address / 8, address % 8, bit, "");
            if (write)
            {
                if (f.Length != 35 + length || f[31] != 0 || f[32] != (bit ? 3 : 4) || PlcWire.Be(f, 33) != (bit ? count : length * 8)) throw new ArgumentException("写入长度或类型不正确。");
                memory.Write(a, f[35..]); return PlcWire.S7Ack(f, [5, 1], [0xFF]);
            }
            if (f.Length != 31) throw new ArgumentException("读取请求不应包含数据。");
            byte[] data = memory.Read(a, length), header = [0xFF, bit ? (byte)3 : (byte)4, 0, 0]; PlcWire.PutBe(header, 2, bit ? count : length * 8); return PlcWire.S7Ack(f, [4, 1], [.. header, .. data]);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException) { return PlcWire.S7Ack(f, [f[17], 1], write ? [5] : [5, 0, 0, 0]); }
    }
    private byte[] Mc(byte[] f)
    {
        if (f.Length < 11 || f[0] != 0x50 || f[1] != 0) throw new IOException("需要 MC 3E 二进制请求。");
        ushort error = 0; byte[] data = [];
        try
        {
            if (f.Length < 21) throw new ArgumentException("MC 请求不完整。");
            int cmd = PlcWire.Le(f, 11), sub = PlcWire.Le(f, 13), count = PlcWire.Le(f, 19); bool bit = sub == 1;
            if (cmd is not (0x0401 or 0x1401) || sub > 1) { error = 0xC059; throw new ArgumentException("功能不支持。"); }
            int address = f[15] | (f[16] << 8) | (f[17] << 16); byte area = f[18];
            if (area is not (0xA8 or 0xB4 or 0xAF or 0x90 or 0x9C or 0x9D or 0x92 or 0xA0)) throw new ArgumentException("设备区不支持。");
            var a = new PlcAddress(memory.Protocol, area, 0, address, 0, bit, "");
            if (count is < 1 or > 960 || bit != a.McBit) throw new ArgumentException("设备类型或数量不支持。");
            int length = bit ? count : count * 2;
            if (cmd == 0x1401)
            { if (f.Length != 21 + (bit ? (count + 1) / 2 : length)) throw new ArgumentException("长度不匹配。"); memory.Write(a, bit ? PlcWire.UnpackBits(f[21..], count) : f[21..]); }
            else { if (f.Length != 21) throw new ArgumentException("读取请求不应包含数据。"); data = memory.Read(a, length); if (bit) data = PlcWire.PackBits(data); }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException) { if (error == 0) error = 0xC051; data = []; }
        var reply = new byte[11 + data.Length]; reply[0] = 0xD0; f.AsSpan(2, 5).CopyTo(reply.AsSpan(2)); PlcWire.PutLe(reply, 7, data.Length + 2); PlcWire.PutLe(reply, 9, error); data.CopyTo(reply, 11); return reply;
    }
    private byte[] Fins(byte[] f)
    {
        if (f.Length < 16 || PlcWire.U32(f, 12) != 0) throw new IOException("FINS TCP 请求格式错误。");
        if (_state == 0)
        {
            if (f.Length != 20 || PlcWire.U32(f, 8) != 0 || PlcWire.U32(f, 16) > 254) throw new IOException("需要 FINS 节点协商请求。");
            _node = f[19] == 0 ? assignedNode : f[19];
            if (_node == 1) return PlcWire.Fins(1, [], 0x20);
            byte[] nodes = new byte[8]; PlcWire.Put32(nodes, 0, _node); PlcWire.Put32(nodes, 4, 1); _state = 1; return PlcWire.Fins(1, nodes);
        }
        if (f.Length < 28 || PlcWire.U32(f, 8) != 2) throw new IOException("需要 FINS 数据报文。");
        byte[] header = f[16..28]; header[0] = 0xC0; f.AsSpan(22, 3).CopyTo(header.AsSpan(3)); f.AsSpan(19, 3).CopyTo(header.AsSpan(6));
        ushort error = 0; byte[] data = [];
        try
        {
            if (f[16] != 0x80 || f[20] != 1 || f[23] != _node || f[21] != 0 || f[19] != 0 || f[22] != 0) { error = 0x0501; throw new ArgumentException("节点/路由不匹配。"); }
            if (PlcWire.Be(f, 26) is not (0x0101 or 0x0102)) { error = 0x0401; throw new ArgumentException("命令不支持。"); }
            if (f.Length < 34) throw new ArgumentException("请求不完整。");
            bool bit = f[28] < 0x80, write = f[27] == 2; byte area = (byte)(bit ? f[28] + 0x80 : f[28]);
            if (area is not (0x82 or 0xB0 or 0xB1 or 0xB2 or 0xB3) || (!bit && f[31] != 0)) throw new ArgumentException("数据区或位号无效。");
            int count = PlcWire.Be(f, 32), length = bit ? count : count * 2;
            if (count is < 1 or > 999) throw new ArgumentException("数量越界。");
            var a = new PlcAddress(memory.Protocol, area, 0, PlcWire.Be(f, 29), f[31], bit, "");
            if (write) { if (f.Length != 34 + length) throw new ArgumentException("写入长度不匹配。"); memory.Write(a, f[34..]); }
            else { if (f.Length != 34) throw new ArgumentException("读取请求不应包含数据。"); data = memory.Read(a, length); }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException) { if (error == 0) error = 0x1103; data = []; }
        return PlcWire.Fins(2, [.. header, (byte)(error >> 8), (byte)error, .. data]);
    }
}
