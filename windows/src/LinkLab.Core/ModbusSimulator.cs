using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Ports;
using System.Net;
using System.Net.Sockets;

namespace LinkLab.Core;

public enum DataArea { Coils, DiscreteInputs, HoldingRegisters, InputRegisters }

public sealed class ModbusMemory
{
    private readonly ushort[][] _areas = [new ushort[65536], new ushort[65536], new ushort[65536], new ushort[65536]];
    private readonly object _sync = new();
    public event Action? Changed;
    public ushort Get(DataArea area, ushort address) { lock (_sync) return _areas[(int)area][address]; }
    public void Set(DataArea area, ushort address, ushort value)
    {
        if (area is DataArea.Coils or DataArea.DiscreteInputs && value > 1) throw new ArgumentException("线圈与离散输入的值只能为 0 / 1。");
        lock (_sync) _areas[(int)area][address] = value;
        Changed?.Invoke();
    }
    public byte[] Process(ReadOnlySpan<byte> pdu)
    {
        byte fc = pdu.Length == 0 ? (byte)0 : pdu[0];
        byte[] Error(byte code) => [(byte)(fc | 0x80), code];
        if (fc is not (1 or 2 or 3 or 4 or 5 or 6 or 15 or 16)) return Error(1);
        if (pdu.Length < 5) return Error(3);
        int addr = BinaryPrimitives.ReadUInt16BigEndian(pdu[1..]);
        int amount = BinaryPrimitives.ReadUInt16BigEndian(pdu[3..]);
        int quantity = fc is 5 or 6 ? 1 : amount;
        int max = fc switch { 1 or 2 => 2000, 3 or 4 => 125, 15 => 1968, 16 => 123, _ => 1 };
        if (quantity < 1 || quantity > max) return Error(3);
        if (addr + quantity > 65536) return Error(2);
        if (fc is not (15 or 16) && pdu.Length != 5) return Error(3);
        if (fc == 5 && amount is not (0 or 0xFF00)) return Error(3);
        if (fc is 15 or 16)
        {
            int byteCount = fc == 15 ? (quantity + 7) / 8 : quantity * 2;
            if (pdu.Length != 6 + byteCount || pdu[5] != byteCount) return Error(3);
        }
        byte[] result;
        lock (_sync)
        {
            if (fc is 1 or 2)
            {
                byte[] packed = new byte[(quantity + 7) / 8];
                for (int i = 0; i < quantity; i++) if (_areas[fc - 1][addr + i] != 0) packed[i / 8] |= (byte)(1 << (i % 8));
                return [fc, (byte)packed.Length, .. packed];
            }
            if (fc is 3 or 4)
            {
                byte[] data = new byte[quantity * 2];
                for (int i = 0; i < quantity; i++) BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(i * 2), _areas[fc - 1][addr + i]);
                return [fc, (byte)data.Length, .. data];
            }
            if (fc == 5) _areas[0][addr] = (ushort)(amount == 0 ? 0 : 1);
            if (fc == 6) _areas[2][addr] = (ushort)amount;
            if (fc == 15) for (int i = 0; i < quantity; i++) _areas[0][addr + i] = (ushort)((pdu[6 + i / 8] >> (i % 8)) & 1);
            if (fc == 16) for (int i = 0; i < quantity; i++) _areas[2][addr + i] = BinaryPrimitives.ReadUInt16BigEndian(pdu[(6 + i * 2)..]);
            result = pdu[..5].ToArray();
        }
        Changed?.Invoke(); return result;
    }
}

public interface IModbusSimulator : IAsyncDisposable
{
    event Action<WireEvent>? Traffic;
    Task StartAsync(CancellationToken ct = default);
}

public sealed class ModbusTcpSimulator(ModbusMemory memory, byte unit, string bind, int port) : IModbusSimulator
{
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<TcpClient, Task> _clients = new();
    private TcpListener? _listener;
    private Task? _accept;
    public int BoundPort { get; private set; }
    public event Action<WireEvent>? Traffic;
    public Task StartAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _listener = new TcpListener(IPAddress.Parse(bind), port); _listener.Start();
        BoundPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
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
                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _clients[client] = completion.Task;
                _ = Serve(client, completion);
            }
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or ObjectDisposedException)
        { if (!_stop.IsCancellationRequested) Traffic?.Invoke(new(DateTimeOffset.Now, "Modbus TCP 模拟", "ERR", [], Note: ex.Message)); }
    }
    private async Task Serve(TcpClient client, TaskCompletionSource completion)
    {
        string peer = client.Client.RemoteEndPoint!.ToString()!;
        try
        {
            client.NoDelay = true; var stream = client.GetStream();
            while (!_stop.IsCancellationRequested)
            {
                var frame = await ModbusIo.ReadTcpAsync(stream, _stop.Token);
                Traffic?.Invoke(new(DateTimeOffset.Now, "Modbus TCP 模拟", "RX", frame, peer));
                if (frame[6] != unit) continue;
                var result = memory.Process(frame.AsSpan(7));
                var response = ModbusProtocol.TcpFrame(BinaryPrimitives.ReadUInt16BigEndian(frame), unit, result);
                await stream.WriteAsync(response, _stop.Token);
                Traffic?.Invoke(new(DateTimeOffset.Now, "Modbus TCP 模拟", "TX", response, peer));
            }
        }
        catch (EndOfStreamException) { }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        { if (!_stop.IsCancellationRequested) Traffic?.Invoke(new(DateTimeOffset.Now, "Modbus TCP 模拟", "ERR", [], peer, ex.Message)); }
        finally { client.Dispose(); _clients.TryRemove(client, out _); completion.TrySetResult(); }
    }
    public async ValueTask DisposeAsync()
    {
        _stop.Cancel(); _listener?.Stop();
        if (_accept is not null) await _accept.ConfigureAwait(false);
        var entries = _clients.ToArray(); foreach (var c in entries) c.Key.Dispose();
        await Task.WhenAll(entries.Select(c => c.Value)).ConfigureAwait(false);
    }
}

public sealed class ModbusRtuSimulator(ModbusMemory memory, byte unit, SerialSettings settings) : IModbusSimulator
{
    private readonly CancellationTokenSource _stop = new();
    private SerialPort? _port;
    private Task? _loop;
    public event Action<WireEvent>? Traffic;
    public Task StartAsync(CancellationToken ct = default)
    {
        if (unit is < 1 or > 247) throw new ArgumentException("RTU 从站站号必须为 1–247。");
        ct.ThrowIfCancellationRequested(); _port = settings.Open(); _port.ReadTimeout = Math.Max(2, (int)Math.Ceiling(settings.SilentMs));
        _loop = Task.Run(Loop); return Task.CompletedTask;
    }
    private void Loop()
    {
        var data = new List<byte>();
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    int b = _port!.ReadByte(); if (b >= 0) data.Add((byte)b);
                    if (data.Count > 256) data.Clear();
                }
                catch (TimeoutException)
                {
                    if (data.Count == 0) continue;
                    var frame = data.ToArray(); data.Clear();
                    Traffic?.Invoke(new(DateTimeOffset.Now, "Modbus RTU 模拟", "RX", frame, settings.Port));
                    if (!ByteTools.ValidRtu(frame) || (frame[0] != unit && frame[0] != 0)) continue;
                    if (frame[0] == 0 && frame[1] is not (5 or 6 or 15 or 16)) continue;
                    var pdu = memory.Process(frame.AsSpan(1, frame.Length - 3));
                    if (frame[0] == 0) continue;
                    var reply = ByteTools.AppendChecksum([unit, .. pdu], ChecksumKind.Crc16Modbus);
                    _port!.Write(reply, 0, reply.Length);
                    Traffic?.Invoke(new(DateTimeOffset.Now, "Modbus RTU 模拟", "TX", reply, settings.Port));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        { if (!_stop.IsCancellationRequested) Traffic?.Invoke(new(DateTimeOffset.Now, "Modbus RTU 模拟", "ERR", [], Note: ex.Message)); }
    }
    public async ValueTask DisposeAsync()
    {
        _stop.Cancel(); if (_loop is not null) await _loop.ConfigureAwait(false); _port?.Dispose();
    }
}
