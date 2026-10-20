using System.Collections.Concurrent;
using System.IO.Ports;
using System.Net;
using System.Net.Sockets;

namespace LinkLab.Core;

public sealed record WireEvent(DateTimeOffset Time, string Source, string Direction, byte[] Data, string Peer = "", string Note = "");
public sealed record SerialSettings(string Port = "COM1", int Baud = 9600, int Bits = 8, Parity Parity = Parity.None, StopBits Stop = StopBits.One, bool Rts = false, bool Dtr = false)
{
    public SerialPort Open()
    {
        var port = new SerialPort(Port, Baud, Parity, Bits, Stop) { ReadTimeout = 100, WriteTimeout = 2000, RtsEnable = Rts, DtrEnable = Dtr };
        try { port.Open(); return port; } catch { port.Dispose(); throw; }
    }
    public double SilentMs => Math.Max(1.75, 3.5 * (1 + Bits + (Parity == Parity.None ? 0 : 1) + (Stop == StopBits.Two ? 2 : 1)) * 1000 / Baud);
}

public abstract class RawTransport(string name) : IAsyncDisposable
{
    public string Name { get; } = name;
    public bool IsOpen { get; protected set; }
    public event Action<WireEvent>? Traffic;
    public event Action? Changed;
    public virtual string[] Peers => [];
    public abstract Task StartAsync(CancellationToken ct = default);
    public abstract Task SendAsync(byte[] data, string? peer = null, CancellationToken ct = default);
    public abstract ValueTask DisposeAsync();
    protected void Emit(string direction, byte[] data, string peer = "", string note = "") => Traffic?.Invoke(new(DateTimeOffset.Now, Name, direction, data, peer, note));
    protected void State() => Changed?.Invoke();
}

public sealed class SerialTransport(SerialSettings settings) : RawTransport($"串口 {settings.Port}")
{
    private SerialPort? _port;
    private readonly CancellationTokenSource _stop = new();
    private Task? _reader;
    public override Task StartAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _port = settings.Open(); IsOpen = true; State();
        _reader = Task.Run(() =>
        {
            var buffer = new byte[8192];
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    try { int n = _port.Read(buffer, 0, buffer.Length); if (n > 0) Emit("RX", buffer[..n], settings.Port); }
                    catch (TimeoutException) { }
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
            { if (!_stop.IsCancellationRequested) Emit("ERR", [], note: ex.Message); }
            finally { IsOpen = false; State(); }
        });
        return Task.CompletedTask;
    }
    public override Task SendAsync(byte[] data, string? peer = null, CancellationToken ct = default) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        if (!IsOpen || _port is null) throw new IOException("串口未打开。");
        lock (_port) _port.Write(data, 0, data.Length);
        Emit("TX", data, settings.Port);
    }, ct);
    public override async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        if (_reader is not null) await _reader.ConfigureAwait(false);
        _port?.Dispose(); IsOpen = false; State(); _stop.Dispose();
    }
}

public enum NetworkMode { TcpClient, TcpServer, Udp }
public sealed class NetworkTransport(NetworkMode mode, string host, int port, string bindHost, int bindPort) : RawTransport(mode switch { NetworkMode.TcpClient => $"TCP → {host}:{port}", NetworkMode.TcpServer => $"TCP 监听 {bindHost}:{bindPort}", _ => $"UDP {bindHost}:{bindPort}" })
{
    private sealed class Peer(TcpClient client) { public TcpClient Client { get; } = client; public SemaphoreSlim SendLock { get; } = new(1, 1); }
    private readonly ConcurrentDictionary<string, Peer> _clients = new();
    private readonly ConcurrentDictionary<int, Task> _tasks = new();
    private int _nextTask;
    private readonly CancellationTokenSource _stop = new();
    private TcpListener? _listener;
    private UdpClient? _udp;
    private IPEndPoint? _remote;
    public bool Echo { get; set; }
    public byte[]? AutoReply { get; set; }
    public int BoundPort { get; private set; }
    public override string[] Peers => _clients.Keys.Order().ToArray();

    public override async Task StartAsync(CancellationToken ct = default)
    {
        try
        {
            if (mode == NetworkMode.TcpClient)
            {
                var client = new TcpClient();
                try { await client.ConnectAsync(host, port, ct).ConfigureAwait(false); }
                catch { client.Dispose(); throw; }
                IsOpen = true; Add(client);
            }
            else if (mode == NetworkMode.TcpServer)
            {
                _listener = new TcpListener(IPAddress.Parse(bindHost), bindPort); _listener.Start();
                BoundPort = ((IPEndPoint)_listener.LocalEndpoint).Port; IsOpen = true;
                Track(AcceptLoop());
            }
            else
            {
                _udp = new UdpClient(new IPEndPoint(IPAddress.Parse(bindHost), bindPort)); _udp.EnableBroadcast = true;
                BoundPort = ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;
                var ips = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
                _remote = new IPEndPoint(ips.First(x => x.AddressFamily == _udp.Client.AddressFamily), port);
                IsOpen = true; Track(UdpLoop());
            }
            State();
        }
        catch { await DisposeAsync(); throw; }
    }
    private void Add(TcpClient client)
    {
        client.NoDelay = true;
        var id = client.Client.RemoteEndPoint!.ToString()!;
        var item = new Peer(client);
        _clients[id] = item; State(); Track(ReadLoop(id, item));
    }
    private void Track(Task task)
    {
        int key = Interlocked.Increment(ref _nextTask); _tasks[key] = task;
        _ = task.ContinueWith(completed => _tasks.TryRemove(key, out _), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
    private async Task AcceptLoop()
    {
        try { while (!_stop.IsCancellationRequested) { var c = await _listener!.AcceptTcpClientAsync(_stop.Token); if (_clients.Count >= 64) c.Dispose(); else Add(c); } }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException) { if (!_stop.IsCancellationRequested) Emit("ERR", [], note: ex.Message); }
    }
    private async Task ReadLoop(string id, Peer item)
    {
        byte[] buffer = new byte[16384];
        try
        {
            var stream = item.Client.GetStream();
            while (!_stop.IsCancellationRequested)
            {
                int n = await stream.ReadAsync(buffer, _stop.Token); if (n == 0) break;
                var bytes = buffer[..n]; Emit("RX", bytes, id, "TCP 字节流片段");
                var reply = Echo ? bytes : AutoReply;
                if (reply is { Length: > 0 }) await SendAsync(reply, id, _stop.Token);
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { if (!_stop.IsCancellationRequested) Emit("ERR", [], id, ex.Message); }
        finally { _clients.TryRemove(id, out _); item.Client.Dispose(); if (mode == NetworkMode.TcpClient) IsOpen = false; State(); }
    }
    private async Task UdpLoop()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var msg = await _udp!.ReceiveAsync(_stop.Token); Emit("RX", msg.Buffer, msg.RemoteEndPoint.ToString());
                var reply = Echo ? msg.Buffer : AutoReply;
                if (reply is { Length: > 0 }) { await _udp.SendAsync(reply, msg.RemoteEndPoint, _stop.Token); Emit("TX", reply, msg.RemoteEndPoint.ToString(), "自动回复"); }
            }
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or ObjectDisposedException) { if (!_stop.IsCancellationRequested) Emit("ERR", [], note: ex.Message); }
        finally { IsOpen = false; State(); }
    }
    public override async Task SendAsync(byte[] data, string? peer = null, CancellationToken ct = default)
    {
        if (!IsOpen) throw new IOException("连接未建立。");
        if (mode == NetworkMode.Udp)
        {
            if (data.Length > 65507) throw new ArgumentException("UDP 数据报最多 65507 字节。");
            await _udp!.SendAsync(data, _remote, ct); Emit("TX", data, _remote!.ToString()); return;
        }
        var targets = string.IsNullOrWhiteSpace(peer) || peer == "全部客户端" ? _clients.ToArray() : _clients.Where(x => x.Key == peer).ToArray();
        if (targets.Length == 0) throw new IOException("没有可发送的客户端。");
        foreach (var entry in targets)
        {
            await entry.Value.SendLock.WaitAsync(ct);
            try { await entry.Value.Client.GetStream().WriteAsync(data, ct); Emit("TX", data, entry.Key); }
            finally { entry.Value.SendLock.Release(); }
        }
    }
    public override async ValueTask DisposeAsync()
    {
        if (_stop.IsCancellationRequested) return;
        _stop.Cancel(); _listener?.Stop(); _udp?.Dispose();
        foreach (var p in _clients.Values) p.Client.Dispose();
        await Task.WhenAll(_tasks.Values.ToArray()).ConfigureAwait(false);
        _clients.Clear(); IsOpen = false; State();
    }
}
