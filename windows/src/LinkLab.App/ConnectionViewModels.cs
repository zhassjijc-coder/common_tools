using System.Collections.ObjectModel;
using System.IO.Ports;
using System.Text;
using System.Windows;
using LinkLab.Core;

namespace LinkLab.App;

public sealed class SerialConfigVm : Observable
{
    [Saved] public string Port { get; set; } = "COM1";
    [Saved] public string Baud { get; set; } = "9600";
    [Saved] public int Bits { get; set; } = 8;
    [Saved] public Parity Parity { get; set; }
    [Saved] public StopBits Stop { get; set; } = StopBits.One;
    [Saved] public bool Rts { get; set; }
    [Saved] public bool Dtr { get; set; }
    public int[] BitOptions { get; } = [5, 6, 7, 8];
    public Parity[] ParityOptions { get; } = Enum.GetValues<Parity>();
    public StopBits[] StopOptions { get; } = [StopBits.One, StopBits.Two, StopBits.OnePointFive];
    public ObservableCollection<string> Ports { get; } = [];
    public Command RefreshCommand { get; }
    public SerialConfigVm() { RefreshCommand = new(Refresh); Refresh(); }
    private void Refresh()
    {
        Ports.Clear();
        try { foreach (var p in SerialPort.GetPortNames().Order()) Ports.Add(p); } catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { }
    }
    public SerialSettings Settings()
    {
        if (string.IsNullOrWhiteSpace(Port)) throw new ArgumentException("请填写 COM 端口。");
        int baud = ByteTools.Number(Baud);
        if (baud is < 50 or > 4000000) throw new ArgumentException("波特率范围为 50–4000000。");
        return new(Port.Trim(), baud, Bits, Parity, Stop, Rts, Dtr);
    }
}

public sealed class SenderVm : PageVm
{
    private readonly Func<RawTransport?> _transport;
    private readonly Func<string?> _peer;
    private CancellationTokenSource? _periodic;
    private Task? _loop;
    [Saved] public string Payload { get; set; } = "01 03 00 00 00 0A";
    [Saved] public string Format { get; set; } = "HEX";
    [Saved] public ChecksumKind Checksum { get; set; }
    [Saved] public string Interval { get; set; } = "1000";
    [Saved] public bool CrLf { get; set; }
    public string[] Formats { get; } = ["HEX", "UTF-8", "ASCII"];
    public ChecksumKind[] Checksums { get; } = Enum.GetValues<ChecksumKind>();
    public string PeriodicText => _periodic is null ? "开始定时发送" : "停止定时发送";
    public AsyncCommand SendCommand { get; }
    public Command PeriodicCommand { get; }
    public SenderVm(Hub hub, Func<RawTransport?> transport, Func<string?>? peer = null) : base(hub)
    {
        _transport = transport; _peer = peer ?? (() => null);
        SendCommand = Async(() => Send()); PeriodicCommand = new(TogglePeriodic);
    }
    private byte[] Bytes()
    {
        var data = Format switch { "HEX" => ByteTools.ParseHex(Payload), "ASCII" => Encoding.ASCII.GetBytes(Payload), _ => Encoding.UTF8.GetBytes(Payload) };
        if (CrLf) data = [.. data, 13, 10];
        if (data.Length == 0) throw new ArgumentException("发送内容不能为空。");
        if (data.Length > 65507) throw new ArgumentException("单次发送最多 65507 字节。");
        return ByteTools.AppendChecksum(data, Checksum);
    }
    public async Task Send(CancellationToken ct = default)
    {
        var t = _transport();
        if (t is null || !t.IsOpen) throw new System.IO.IOException("请先建立连接。");
        await t.SendAsync(Bytes(), _peer(), ct); Hub.Notice = "发送完成";
    }
    private void TogglePeriodic()
    {
        if (_periodic is not null) { _periodic.Cancel(); return; }
        try
        {
            int ms = Int(Interval, 20, 3600000, "发送间隔");
            _ = Bytes();
            if (_transport()?.IsOpen != true) throw new InvalidOperationException("请先建立连接。");
            _periodic = new(); Raise(nameof(PeriodicText)); _loop = PeriodicLoop(ms, _periodic.Token);
        }
        catch (Exception ex) { Hub.Error(ex); }
    }
    private async Task PeriodicLoop(int ms, CancellationToken ct)
    {
        try { while (!ct.IsCancellationRequested) { await Send(ct); await Task.Delay(ms, ct); } }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Hub.Error(ex); }
        finally { _periodic?.Dispose(); _periodic = null; Raise(nameof(PeriodicText)); }
    }
    public async Task Stop() { _periodic?.Cancel(); if (_loop is not null) await _loop; }
}

public sealed class SerialVm : PageVm
{
    private SerialTransport? _transport;
    public SerialConfigVm Config { get; } = new();
    public SenderVm Sender { get; }
    public bool Connected => _transport?.IsOpen == true;
    public bool CanConfigure => !Connected;
    public string Status => Connected ? $"已连接 · {_transport!.Name}" : "未连接 · 选择串口后打开";
    public string ConnectText => Connected ? "关闭串口" : "打开串口";
    public AsyncCommand ConnectCommand { get; }
    public SerialVm(Hub hub) : base(hub)
    {
        Sender = new(hub, () => _transport) { Checksum = ChecksumKind.Crc16Modbus };
        ConnectCommand = Async(Toggle);
    }
    private void Changed() { Raise(nameof(Connected)); Raise(nameof(CanConfigure)); Raise(nameof(Status)); Raise(nameof(ConnectText)); }
    private async Task Toggle()
    {
        if (Connected) { await Close(); return; }
        await Close(); var t = new SerialTransport(Config.Settings());
        try
        {
            t.Changed += () => Application.Current.Dispatcher.BeginInvoke(Changed);
            await Task.Run(() => t.StartAsync()); _transport = t; Hub.Attach(t); Changed(); Hub.Notice = Status;
        }
        catch { await t.DisposeAsync(); throw; }
    }
    public async Task Close()
    {
        await Sender.Stop();
        if (_transport is not null) { await _transport.DisposeAsync(); Hub.Detach(_transport); _transport = null; }
        Changed();
    }
}

public sealed class NetworkVm : PageVm
{
    private NetworkTransport? _transport;
    private int _mode;
    [Saved] public int Mode { get => _mode; set { if (Set(ref _mode, value)) { Raise(nameof(IsClient)); Raise(nameof(IsServer)); Raise(nameof(IsUdp)); Raise(nameof(ConnectText)); } } }
    public string[] Modes { get; } = ["TCP 客户端", "TCP 服务端", "UDP 收发端"];
    public bool IsClient => Mode != 1;
    public bool IsServer => Mode != 0;
    public bool IsUdp => Mode == 2;
    [Saved] public string Host { get; set; } = "127.0.0.1";
    [Saved] public string Port { get; set; } = "9000";
    [Saved] public string BindHost { get; set; } = "127.0.0.1";
    [Saved] public string BindPort { get; set; } = "9000";
    private int _replyMode;
    [Saved] public int ReplyMode { get => _replyMode; set { Set(ref _replyMode, value); ApplyReply(); } }
    private string _replyHex = "4F 4B";
    [Saved] public string ReplyHex { get => _replyHex; set { Set(ref _replyHex, value); ApplyReply(); } }
    public string[] Replies { get; } = ["不自动回复", "原样回显", "固定 HEX 回复"];
    public string SelectedPeer { get; set; } = "全部客户端";
    public ObservableCollection<string> Peers { get; } = ["全部客户端"];
    public SenderVm Sender { get; }
    public bool Connected => _transport?.IsOpen == true;
    public bool CanConfigure => !Connected;
    public string ConnectText => Connected ? "停止连接" : Mode == 0 ? "连接设备" : "开始监听";
    public string Status => !Connected ? "尚未连接" : Mode == 0 ? $"已连接 · {_transport!.Name}" : $"运行中 · {BindHost}:{_transport!.BoundPort}" + (Mode == 1 ? $" · {Math.Max(0, Peers.Count - 1)} 个客户端" : " · UDP 收发就绪");
    public AsyncCommand ConnectCommand { get; }
    public NetworkVm(Hub hub) : base(hub)
    { Sender = new(hub, () => _transport, () => SelectedPeer); ConnectCommand = Async(Toggle); }
    private void ApplyReply()
    {
        if (_transport is null) return;
        try { _transport.Echo = ReplyMode == 1; _transport.AutoReply = ReplyMode == 2 ? ByteTools.ParseHex(ReplyHex) : null; }
        catch (FormatException ex) { _transport.AutoReply = null; Hub.Error(ex); }
    }
    private void Changed()
    {
        string selected = SelectedPeer;
        Peers.Clear(); Peers.Add("全部客户端");
        if (_transport is not null) foreach (var p in _transport.Peers) Peers.Add(p);
        SelectedPeer = Peers.Contains(selected) ? selected : "全部客户端"; Raise(nameof(SelectedPeer));
        Raise(nameof(Connected)); Raise(nameof(CanConfigure)); Raise(nameof(ConnectText)); Raise(nameof(Status));
    }
    private async Task Toggle()
    {
        if (Connected) { await Close(); return; }
        await Close();
        if (ReplyMode == 2) _ = ByteTools.ParseHex(ReplyHex);
        var t = new NetworkTransport((NetworkMode)Mode, Host.Trim(), Int(Port, 1, 65535, "目标端口"), BindHost.Trim(), Int(BindPort, 0, 65535, "本地端口"));
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            t.Changed += () => Application.Current.Dispatcher.BeginInvoke(Changed);
            await t.StartAsync(timeout.Token); _transport = t; Hub.Attach(t); ApplyReply(); Changed(); Hub.Notice = Status;
        }
        catch { await t.DisposeAsync(); throw; }
    }
    public async Task Close()
    {
        await Sender.Stop();
        if (_transport is not null) { await _transport.DisposeAsync(); Hub.Detach(_transport); _transport = null; }
        Changed();
    }
}
