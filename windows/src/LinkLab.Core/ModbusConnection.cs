using System.Buffers.Binary;
using System.IO.Ports;
using System.Net.Sockets;

namespace LinkLab.Core;

public interface IModbusConnection : IAsyncDisposable
{
    event Action<WireEvent>? Traffic;
    Task<ushort[]> ExecuteAsync(ModbusRequest request, int timeoutMs, CancellationToken ct = default);
}

public sealed class ModbusTcpConnection(string host, int port) : IModbusConnection
{
    private TcpClient? _client;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ushort _transaction;
    public event Action<WireEvent>? Traffic;
    public async Task<ushort[]> ExecuteAsync(ModbusRequest request, int timeoutMs, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(timeoutMs);
        var token = deadline.Token;
        try
        {
            var frame = request.Tcp(++_transaction);
            if (_client is null) { _client = new TcpClient { NoDelay = true }; await _client.ConnectAsync(host, port, token); }
            var stream = _client.GetStream();
            await stream.WriteAsync(frame, token);
            Traffic?.Invoke(new(DateTimeOffset.Now, "Modbus TCP 主站", "TX", frame, $"{host}:{port}"));
            var response = await ModbusIo.ReadTcpAsync(stream, token);
            Traffic?.Invoke(new(DateTimeOffset.Now, "Modbus TCP 主站", "RX", response, $"{host}:{port}"));
            if (BinaryPrimitives.ReadUInt16BigEndian(response) != _transaction || response[6] != request.Unit) throw new IOException("响应事务 ID 或站号不匹配。");
            return ModbusProtocol.ParseResponse(request, response.AsSpan(7));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { Close(); throw new TimeoutException($"设备在 {timeoutMs} ms 内未响应。"); }
        catch (ModbusException) { throw; }
        catch { Close(); throw; }
        finally { _gate.Release(); }
    }
    private void Close() { _client?.Dispose(); _client = null; }
    public ValueTask DisposeAsync() { Close(); return ValueTask.CompletedTask; }
}

public sealed class ModbusRtuConnection(SerialSettings settings) : IModbusConnection
{
    private SerialPort? _port;
    private readonly SemaphoreSlim _gate = new(1, 1);
    public event Action<WireEvent>? Traffic;
    public async Task<ushort[]> ExecuteAsync(ModbusRequest request, int timeoutMs, CancellationToken ct = default)
    {
        if (request.Unit is < 1 or > 247) throw new ArgumentException("RTU 主站交互使用站号 1–247；广播请使用报文工作台。");
        await _gate.WaitAsync(ct);
        try
        {
            var frame = request.Rtu();
            _port ??= settings.Open();
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Ceiling(settings.SilentMs)), ct);
            return await Task.Run(() =>
            {
                _port.DiscardInBuffer(); _port.Write(frame, 0, frame.Length);
                Traffic?.Invoke(new(DateTimeOffset.Now, "Modbus RTU 主站", "TX", frame, settings.Port));
                var data = new List<byte>(); var timer = System.Diagnostics.Stopwatch.StartNew();
                int expected = 0;
                while (timer.ElapsedMilliseconds < timeoutMs)
                {
                    ct.ThrowIfCancellationRequested();
                    try { data.Add((byte)_port.ReadByte()); } catch (TimeoutException) { continue; }
                    if (data.Count == 3) expected = (data[1] & 0x80) != 0 ? 5 : data[1] is >= 1 and <= 4 ? 5 + data[2] : 8;
                    if (expected != 0 && data.Count == expected)
                    {
                        var response = data.ToArray();
                        Traffic?.Invoke(new(DateTimeOffset.Now, "Modbus RTU 主站", "RX", response, settings.Port));
                        if (!ByteTools.ValidRtu(response)) throw new IOException("响应 CRC 校验失败。");
                        if (response[0] != request.Unit) throw new IOException("响应站号不匹配。");
                        return ModbusProtocol.ParseResponse(request, response.AsSpan(1, response.Length - 3));
                    }
                }
                throw new TimeoutException($"设备在 {timeoutMs} ms 内未返回完整响应（收到 {data.Count} 字节）。");
            }, ct);
        }
        catch (ModbusException) { throw; }
        catch { _port?.Dispose(); _port = null; throw; }
        finally { _gate.Release(); }
    }
    public ValueTask DisposeAsync() { _port?.Dispose(); _port = null; return ValueTask.CompletedTask; }
}

public static class ModbusIo
{
    public static async Task<byte[]> ReadTcpAsync(Stream stream, CancellationToken ct)
    {
        byte[] head = new byte[6]; await stream.ReadExactlyAsync(head, ct);
        int len = BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(4));
        if (head[2] != 0 || head[3] != 0 || len is < 2 or > 254) throw new IOException("非法 MBAP 协议标识或长度。");
        byte[] body = new byte[len]; await stream.ReadExactlyAsync(body, ct);
        return [.. head, .. body];
    }
}
