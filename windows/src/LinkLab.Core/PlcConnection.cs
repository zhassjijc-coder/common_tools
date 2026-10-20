using System.Net.Sockets;

namespace LinkLab.Core;

public sealed class PlcConnection(PlcOptions options) : IAsyncDisposable
{
    private TcpClient? _client;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private int _disposed;
    private bool _connected;
    private ushort _sequence;
    private byte _clientNode, _serverNode;
    private int _pdu = 240;
    public event Action<WireEvent>? Traffic;
    /// <summary>TCP establishment and the protocol's required handshake completed. MC has no application handshake.</summary>
    public bool IsConnected => Volatile.Read(ref _connected) && Volatile.Read(ref _disposed) == 0;
    /// <summary>Establishes TCP and negotiates S7/FINS without reading or writing PLC memory.</summary>
    public async Task ConnectAsync(int timeoutMs, CancellationToken ct = default)
    {
        ValidateTimeout(timeoutMs);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        timeout.CancelAfter(timeoutMs);
        bool entered = false;
        try
        {
            await _gate.WaitAsync(timeout.Token); entered = true;
            await ConnectCore(timeout.Token);
        }
        catch (Exception ex) when (timeout.IsCancellationRequested)
        {
            if (entered) Close();
            throw Cancellation(ex, ct, timeoutMs, connecting: true);
        }
        catch { if (entered) Close(); throw; }
        finally { if (entered) _gate.Release(); }
    }
    public async Task<byte[]> ExecuteAsync(PlcRequest request, int timeoutMs, CancellationToken ct = default)
    {
        request.Validate();
        if (request.Address.Protocol != options.Protocol) throw new ArgumentException("连接和地址的协议不一致。");
        ValidateTimeout(timeoutMs);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token); timeout.CancelAfter(timeoutMs);
        bool entered = false;
        try
        {
            await _gate.WaitAsync(timeout.Token); entered = true;
            await ConnectCore(timeout.Token);
            var frame = PlcWire.Request(request, options, ++_sequence, _clientNode, _serverNode);
            if (options.Protocol == PlcProtocol.SiemensS7 && (frame.Length - 7 > _pdu || 18 + request.ByteCount > _pdu)) throw new ArgumentException($"请求超过设备协商的 PDU {_pdu} 字节，请减少数量。");
            var response = await Exchange(frame, timeout.Token);
            return PlcWire.ParseResponse(request, frame, response);
        }
        catch (Exception ex) when (timeout.IsCancellationRequested) { if (entered) Close(); throw Cancellation(ex, ct, timeoutMs, connecting: false); }
        catch (PlcException) { throw; }
        catch { if (entered) Close(); throw; }
        finally { if (entered) _gate.Release(); }
    }
    private static void ValidateTimeout(int timeoutMs) { if (timeoutMs is < 50 or > 60000) throw new ArgumentOutOfRangeException(nameof(timeoutMs)); }
    private Exception Cancellation(Exception inner, CancellationToken ct, int timeoutMs, bool connecting)
    {
        if (ct.IsCancellationRequested) return new OperationCanceledException("PLC 操作已取消。", inner, ct);
        if (_lifetime.IsCancellationRequested) return new OperationCanceledException("PLC 连接已关闭。", inner, _lifetime.Token);
        return new TimeoutException(connecting ? $"PLC 连接超时（{timeoutMs} ms）：TCP 连接或协议握手未完成，请检查地址、端口与协议。" : $"PLC 在 {timeoutMs} ms 内未响应。写入不会自动重试，请读取确认设备状态。", inner);
    }
    private async Task ConnectCore(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (IsConnected) return;
        if (!Enum.IsDefined(options.Protocol) || string.IsNullOrWhiteSpace(options.Host) || options.Port is < 1 or > 65535) throw new ArgumentException("PLC 协议、主机或端口无效。");
        Close(); _sequence = 0; _clientNode = _serverNode = 0; _pdu = 240;
        var client = new TcpClient { NoDelay = true }; _client = client;
        try
        {
            await client.ConnectAsync(options.Host, options.Port, ct);
            await Handshake(ct);
            ct.ThrowIfCancellationRequested();
            Volatile.Write(ref _connected, true);
        }
        catch { Close(); client.Dispose(); throw; }
    }
    private async Task Handshake(CancellationToken ct)
    {
        if (options.Protocol == PlcProtocol.SiemensS7)
        {
            var f = await Exchange(PlcWire.Cotp(options), ct);
            if (f.Length < 11 || f[5] != 0xD0 || PlcWire.Be(f, 6) != 1) throw new IOException("S7 COTP 连接被拒绝，请检查 TSAP、机架和槽位。");
            f = await Exchange(PlcWire.S7Job(++_sequence, [0xF0, 0, 0, 1, 0, 1, 1, 0xE0]), ct);
            if (f.Length != 27 || f[7] != 0x32 || f[8] != 3 || PlcWire.Be(f, 11) != _sequence || PlcWire.Be(f, 13) != 8 || PlcWire.Be(f, 15) != 0 || PlcWire.Be(f, 17) != 0 || f[19] != 0xF0) throw new IOException("S7 PDU 协商失败。");
            _pdu = PlcWire.Be(f, 25); if (_pdu < 64 || _pdu > 8192) throw new IOException("S7 PDU 长度无效。");
        }
        else if (options.Protocol == PlcProtocol.OmronFinsTcp)
        {
            byte[] node = new byte[4]; PlcWire.Put32(node, 0, options.ClientNode);
            var f = await Exchange(PlcWire.Fins(0, node), ct);
            if (f.Length != 24 || PlcWire.U32(f, 8) != 1 || PlcWire.U32(f, 12) != 0 || PlcWire.U32(f, 16) is < 1 or > 254 || PlcWire.U32(f, 20) is < 1 or > 254) throw new IOException("FINS 节点协商失败，请检查节点号与访问设置。");
            _clientNode = (byte)PlcWire.U32(f, 16); _serverNode = options.ServerNode == 0 ? (byte)PlcWire.U32(f, 20) : options.ServerNode;
        }
    }
    private async Task<byte[]> Exchange(byte[] f, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var stream = (_client ?? throw new OperationCanceledException("PLC 连接已关闭。", ct)).GetStream(); await stream.WriteAsync(f, ct);
        Traffic?.Invoke(new(DateTimeOffset.Now, $"PLC {options.Protocol}", "TX", f, $"{options.Host}:{options.Port}"));
        var reply = await PlcWire.ReadFrame(stream, options.Protocol, ct);
        Traffic?.Invoke(new(DateTimeOffset.Now, $"PLC {options.Protocol}", "RX", reply, $"{options.Host}:{options.Port}")); return reply;
    }
    private void Close() { Volatile.Write(ref _connected, false); Interlocked.Exchange(ref _client, null)?.Dispose(); }
    public async ValueTask DisposeAsync()
    {
        // Cancel DNS/TCP/handshake first, so disposal never waits for the request timeout.
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _lifetime.Cancel();
        Close(); await _gate.WaitAsync(); try { Close(); } finally { _gate.Release(); }
    }
}
