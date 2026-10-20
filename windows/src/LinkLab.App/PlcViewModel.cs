using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using LinkLab.Core;
using Microsoft.Win32;

namespace LinkLab.App;

public sealed class PlcRow : Observable
{
    public string Address { get; init; } = "";
    public string Hex { get; init; } = "";
    private string _value = "";
    public string Value { get => _value; set => Set(ref _value, value); }
}

public sealed class PlcVm : PageVm
{
    private readonly PlcMemory[] _memories = Enum.GetValues<PlcProtocol>().Select(p => new PlcMemory(p)).ToArray();
    private PlcConnection? _connection;
    private PlcOptions? _options;
    private byte[] _lastFrame = [];
    private PlcSimulator? _server;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _requestStop, _poll;
    private Task? _pollTask;
    private Task? _disconnectTask;
    private Task _retiredConnections = Task.CompletedTask;
    private int _settingsVersion;
    private int _protocol, _mode;
    private bool _busy, _disconnecting, _hasError;
    private string _host = "127.0.0.1", _port = "1102", _serverPort = "1102", _address = "DB1.0", _count = "8", _values = "100 200 300 400 500 600 700 800";
    private PlcDataType _type;
    private WordOrder _order;
    private string _rack = "0", _slot = "1", _remoteTsap = "0", _clientNode = "0", _serverNode = "0", _network = "0", _station = "255", _ioNumber = "1023", _unit = "0";
    private string _status = "未连接 · 点击连接 PLC，或在首次读写时自动连接", _serverStatus = "通信模拟器 · 启动后可供外部上位机连接";
    private PlcRequest? _displayRequest;
    private WordOrder _displayOrder;
    [Saved] public int ProtocolIndex { get => _protocol; set { if (value is < 0 or > 2 || !CanSelectProtocol) return; if (Set(ref _protocol, value)) { ConnectionSettingsChanged(); SetDefaults(); Raise(nameof(IsS7)); Raise(nameof(IsMc)); Raise(nameof(IsFins)); Raise(nameof(ProtocolHelp)); } } }
    public PlcProtocol Protocol => (PlcProtocol)ProtocolIndex;
    public string[] Protocols { get; } = ["西门子 · S7 TCP", "三菱 · MC 3E Binary", "欧姆龙 · FINS/TCP"];
    public string[] Modes { get; } = ["客户端 / 监视", "服务器 / 模拟内存"];
    public int Mode { get => _mode; set { Set(ref _mode, value); Raise(nameof(IsClient)); Raise(nameof(IsServer)); } }
    public bool IsClient => Mode == 0;
    public bool IsServer => Mode == 1;
    public bool IsS7 => ProtocolIndex == 0;
    public bool IsMc => ProtocolIndex == 1;
    public bool IsFins => ProtocolIndex == 2;
    public bool Running => _server is not null;
    public bool CanSelectProtocol => !Running && CanConfigure;
    public bool CanConfigure => _poll is null && !_busy && !_disconnecting;
    public bool CanConnect => CanConfigure && !IsConnected;
    public bool IsConnected => _connection?.IsConnected == true;
    public bool HasError { get => _hasError; private set => Set(ref _hasError, value); }
    public string ConnectionState => IsConnected ? (IsMc ? $"MC TCP 已连接 · {_options!.Host}:{_options.Port} · 读写尚需单独验证" : $"{(IsS7 ? "S7" : "FINS/TCP")} 已连接 · {_options!.Host}:{_options.Port} · 协议握手已完成") : _busy ? "正在连接 / 通信…" : "未连接";
    public bool CanConfigureServer => !Running;
    public string ServerToggleText => Running ? "停止服务器" : "启动服务器";
    public string PollText => _poll is null ? "连续监视" : "停止监视";
    public string ProtocolHelp => ProtocolIndex switch
    {
        0 => "DB1.0 / DB1.DBW0 / M0 / I0 / Q0；Bool 使用 DB1.0.0（单个位）。S7-1200/1500 需要允许 PUT/GET 和非优化 DB。300 常用槽位 2，1200/1500 常用 1。",
        1 => "D100 / W100 / R100；Bool 使用 M100 / X10 / Y10 / L0 / B0。X、Y、W、B 地址为十六进制。设备需启用 MC 3E 二进制 TCP。",
        _ => "D100 / CIO0 / W0 / H0 / A0；Bool 使用 D100.0（位号 0–15）。客户端节点 0 为自动分配，目标节点 0 使用握手结果。默认 32 位字序 CDAB，可按 PLC 设置调整。"
    };
    [Saved] public string Host { get => _host; set => SetConnection(ref _host, value); }
    [Saved] public string Port { get => _port; set => SetConnection(ref _port, value); }
    [Saved] public string ServerPort { get => _serverPort; set => Set(ref _serverPort, value); }
    [Saved] public string Bind { get; set; } = "127.0.0.1";
    [Saved] public string Rack { get => _rack; set => SetConnection(ref _rack, value); }
    [Saved] public string Slot { get => _slot; set => SetConnection(ref _slot, value); }
    [Saved] public string RemoteTsap { get => _remoteTsap; set => SetConnection(ref _remoteTsap, value); }
    [Saved] public string ClientNode { get => _clientNode; set => SetConnection(ref _clientNode, value); }
    [Saved] public string ServerNode { get => _serverNode; set => SetConnection(ref _serverNode, value); }
    [Saved] public string Network { get => _network; set => SetConnection(ref _network, value); }
    [Saved] public string Station { get => _station; set => SetConnection(ref _station, value); }
    [Saved] public string IoNumber { get => _ioNumber; set => SetConnection(ref _ioNumber, value); }
    [Saved] public string Unit { get => _unit; set => SetConnection(ref _unit, value); }
    [Saved] public string Timeout { get; set; } = "2000";
    [Saved] public string Interval { get; set; } = "1000";
    [Saved] public string Address { get => _address; set => Set(ref _address, value); }
    [Saved] public string Count { get => _count; set => Set(ref _count, value); }
    [Saved] public string Values { get => _values; set => Set(ref _values, value); }
    [Saved] public PlcDataType DataType { get => _type; set => Set(ref _type, value); }
    [Saved] public WordOrder Order { get => _order; set => Set(ref _order, value); }
    public PlcDataType[] DataTypes { get; } = Enum.GetValues<PlcDataType>();
    public WordOrder[] Orders { get; } = Enum.GetValues<WordOrder>();
    public string Status { get => _status; set => Set(ref _status, value); }
    public string ServerStatus { get => _serverStatus; set => Set(ref _serverStatus, value); }
    public string Frame { get; private set; } = "执行后显示本次读写报文；握手与完整响应在下方收发记录中。";
    public ObservableCollection<PlcRow> ClientRows { get; } = [];
    public ObservableCollection<PlcRow> MemoryRows { get; } = [];
    public PlcMemory Memory => _memories[ProtocolIndex];
    public AsyncCommand ReadCommand { get; }
    public AsyncCommand ConnectCommand { get; }
    public AsyncCommand WriteCommand { get; }
    public AsyncCommand DisconnectCommand { get; }
    public Command PollCommand { get; }
    public AsyncCommand ServerCommand { get; }
    public AsyncCommand LoopbackCommand { get; }
    public Command RefreshCommand { get; }
    public Command ApplyCommand { get; }
    public Command SaveMemoryCommand { get; }
    public Command LoadMemoryCommand { get; }
    public PlcVm(Hub hub) : base(hub)
    {
        for (int p = 0; p < 3; p++)
        {
            var protocol = (PlcProtocol)p; var a = PlcAddress.Parse(protocol, p == 0 ? "DB1.0" : "D100", PlcDataType.UInt16);
            _memories[p].Write(a, Enumerable.Range(1, 8).SelectMany(n => PlcValues.Encode((n * 100).ToString(), PlcDataType.UInt16, PlcValues.DefaultOrder(protocol))).ToArray());
        }
        ConnectCommand = Async(() => Connect()); ReadCommand = Async(() => Execute(false)); WriteCommand = Async(() => Execute(true)); DisconnectCommand = Async(Disconnect);
        PollCommand = new(TogglePoll); ServerCommand = Async(ToggleServer); LoopbackCommand = Async(Loopback);
        RefreshCommand = Safe(Refresh); ApplyCommand = Safe(Apply); SaveMemoryCommand = Safe(SaveMemory); LoadMemoryCommand = Safe(LoadMemory); Refresh();
    }
    private Command Safe(Action action) => new(() => { try { action(); } catch (Exception ex) { Hub.Error(ex); } });
    private void SetConnection(ref string field, string value, [CallerMemberName] string? name = null) { if (Set(ref field, value, name)) ConnectionSettingsChanged(); }
    private void ConnectionSettingsChanged()
    {
        _settingsVersion++; _requestStop?.Cancel();
        if (_connection is not null)
        {
            var old = _connection; _connection = null; _options = null;
            _retiredConnections = Task.WhenAll(_retiredConnections, old.DisposeAsync().AsTask());
            HasError = false; Status = "连接参数已改变 · 原连接已释放，请重新连接";
        }
        Changed();
    }
    private void SetDefaults() { Port = ServerPort = ProtocolIndex switch { 0 => "1102", 1 => "6000", _ => "9600" }; Address = IsS7 ? "DB1.0" : "D100"; DataType = PlcDataType.UInt16; Order = PlcValues.DefaultOrder(Protocol); ClientRows.Clear(); MemoryRows.Clear(); }
    public PlcRequest Build(bool write = false)
    {
        var a = PlcAddress.Parse(Protocol, Address, DataType); int count = Int(Count, 1, 100, "数量");
        byte[]? bytes = write ? Values.Split([' ', ',', ';', '\r', '\n', '，'], StringSplitOptions.RemoveEmptyEntries).SelectMany(v => PlcValues.Encode(v, DataType, Order)).ToArray() : null;
        var request = new PlcRequest(a, DataType, count, bytes); request.Validate(); return request;
    }
    private PlcOptions Options() => new(Protocol, Host.Trim(), Int(Port, 1, 65535, "端口"), (byte)Int(Rack, 0, 7, "机架"), (byte)Int(Slot, 0, 31, "槽位"),
        (byte)Int(ClientNode, 0, 254, "客户端节点"), (byte)Int(ServerNode, 0, 254, "目标节点"), (byte)Int(Network, 0, 255, "网络号"), (byte)Int(Station, 0, 255, "PLC 号"), (ushort)Int(IoNumber, 0, 65535, "I/O 号"), (byte)Int(Unit, 0, 255, "模块站号/单元号"), RemoteTsap: (ushort)Int(RemoteTsap, 0, 65535, "远端 TSAP"));
    private async Task<PlcConnection> Connection(PlcOptions options)
    {
        if (_connection is null || options != _options)
        {
            if (_connection is not null) await _connection.DisposeAsync();
            _options = options; _connection = new(options);
            _connection.Traffic += e => { Hub.Record(e); if (e.Direction == "TX") _lastFrame = e.Data; };
        }
        return _connection;
    }
    public async Task Connect(CancellationToken ct = default)
    {
        if (!CanConfigure) throw new InvalidOperationException("请先停止当前操作，再连接 PLC。");
        await _gate.WaitAsync(ct); _busy = true; HasError = false; Changed();
        try
        {
            if (_disconnecting) throw new OperationCanceledException("正在断开 PLC。", ct);
            int version = _settingsVersion; var options = Options();
            _requestStop = CancellationTokenSource.CreateLinkedTokenSource(ct); Status = $"正在连接 {options.Host}:{options.Port}…";
            var connection = await Connection(options);
            await connection.ConnectAsync(Int(Timeout, 100, 60000, "超时"), _requestStop.Token);
            _requestStop.Token.ThrowIfCancellationRequested();
            if (version != _settingsVersion || !ReferenceEquals(connection, _connection)) throw new OperationCanceledException("连接参数已改变，请重新连接。");
            Status = ConnectionState; Hub.Notice = Status;
        }
        catch (Exception ex) { ReportFailure(ex, connecting: true); throw; }
        finally { _requestStop?.Dispose(); _requestStop = null; _busy = false; Changed(); _gate.Release(); }
    }
    public async Task Execute(bool write, CancellationToken ct = default)
    {
        if (write && _poll is not null) throw new InvalidOperationException("请先停止连续监视，再执行写入。");
        if (_disconnecting || _busy) throw new InvalidOperationException("PLC 正在执行其他操作，请稍后再试。");
        await _gate.WaitAsync(ct); _busy = true; HasError = false; Changed();
        try
        {
            if (_disconnecting) throw new OperationCanceledException("正在断开 PLC。", ct);
            int version = _settingsVersion; var request = Build(write); var options = Options(); var order = Order;
            _requestStop = CancellationTokenSource.CreateLinkedTokenSource(ct); Status = "等待 PLC 响应…"; var timer = Stopwatch.StartNew();
            var connection = await Connection(options);
            byte[] data = await connection.ExecuteAsync(request, Int(Timeout, 100, 60000, "超时"), _requestStop.Token);
            _requestStop.Token.ThrowIfCancellationRequested();
            if (version != _settingsVersion || !ReferenceEquals(connection, _connection)) throw new OperationCanceledException("连接参数已改变，请重新连接。");
            Fill(ClientRows, request, data, order);
            Frame = ByteTools.Hex(_lastFrame); Raise(nameof(Frame));
            Status = $"{(write ? "写入" : "读取")}成功 · {request.Count} 个值 · {timer.ElapsedMilliseconds} ms · {DateTime.Now:HH:mm:ss}"; Hub.Notice = Status;
        }
        catch (Exception ex) { ReportFailure(ex, connecting: false); throw; }
        finally { _requestStop?.Dispose(); _requestStop = null; _busy = false; Changed(); _gate.Release(); }
    }
    private void ReportFailure(Exception ex, bool connecting)
    {
        HasError = ex is not OperationCanceledException;
        Status = ex is OperationCanceledException ? "操作已停止 · 未继续发送请求" : $"{(connecting ? "连接失败" : "操作失败")} · {Host.Trim()}:{Port}：{ex.Message}";
        Hub.Notice = Status;
    }
    private static void Fill(ObservableCollection<PlcRow> rows, PlcRequest request, byte[] data, WordOrder order)
    {
        rows.Clear(); int size = PlcValues.Size(request.Type);
        for (int i = 0; i < request.Count; i++) rows.Add(new() { Address = request.Address.Advance(i, request.Type).Display, Hex = ByteTools.Hex(data.AsSpan(i * size, size)), Value = PlcValues.Decode(data.AsSpan(i * size, size), request.Type, order) });
    }
    public void Refresh() { var req = Build(); Fill(MemoryRows, req, Memory.Read(req.Address, req.ByteCount), Order); _displayRequest = req; _displayOrder = Order; }
    public void Apply()
    {
        if (_displayRequest is null || _displayRequest.Address.Protocol != Protocol) throw new InvalidOperationException("请先载入要编辑的数据区。");
        byte[] data = MemoryRows.SelectMany(x => PlcValues.Encode(x.Value, _displayRequest.Type, _displayOrder)).ToArray();
        Memory.Write(_displayRequest.Address, data); Fill(MemoryRows, _displayRequest, data, _displayOrder); Hub.Notice = "模拟内存已更新，外部客户端可以读取。";
    }
    public async Task ToggleServer()
    {
        if (_server is not null) { await StopServer(); return; }
        var server = new PlcSimulator(Memory, Bind.Trim(), Int(ServerPort, 1, 65535, "监听端口")); server.Traffic += Hub.Record;
        try { await server.StartAsync(); _server = server; ServerStatus = $"正在监听 {Bind}:{ServerPort} · 最多 64 个连接 · FINS 模拟节点 1"; }
        catch { await server.DisposeAsync(); throw; }
        Changed();
    }
    public async Task Loopback()
    {
        await Disconnect(); if (_server is null) await ToggleServer(); Host = "127.0.0.1"; Port = ServerPort; ClientNode = "0"; ServerNode = "0"; Network = "0"; Unit = "0";
        Raise(nameof(ClientNode)); Raise(nameof(ServerNode)); Raise(nameof(Network)); Raise(nameof(Unit)); Mode = 0; await Execute(false);
    }
    private void TogglePoll()
    {
        if (_poll is not null) { _poll.Cancel(); return; }
        try { if (_busy || _disconnecting) throw new InvalidOperationException("请先等待当前 PLC 操作完成。"); _ = Build(); int interval = Int(Interval, 100, 60000, "间隔"); _poll = new(); Changed(); _pollTask = Poll(interval, _poll.Token); } catch (Exception ex) { ReportFailure(ex, false); Hub.Error(ex); }
    }
    private async Task Poll(int interval, CancellationToken ct)
    {
        try { while (!ct.IsCancellationRequested) { await Execute(false, ct); await Task.Delay(interval, ct); } }
        catch (OperationCanceledException) { } catch (Exception ex) { Hub.Error(ex); }
        finally { _poll?.Dispose(); _poll = null; Changed(); }
    }
    public Task Disconnect()
    {
        if (_disconnectTask is { IsCompleted: false }) return _disconnectTask;
        _disconnecting = true; _poll?.Cancel(); _requestStop?.Cancel(); Changed();
        return _disconnectTask = DisconnectCore();
    }
    private async Task DisconnectCore()
    {
        try
        {
            if (_pollTask is not null) await _pollTask;
            await _gate.WaitAsync();
            try { if (_connection is not null) await _connection.DisposeAsync(); _connection = null; _options = null; await _retiredConnections; HasError = false; Status = "客户端连接已释放"; }
            finally { _gate.Release(); }
        }
        finally { _disconnecting = false; Changed(); }
    }
    private async Task StopServer() { if (_server is not null) { await _server.DisposeAsync(); _server = null; } ServerStatus = "服务器已停止，内存保留"; Changed(); }
    public async Task Close() { await Disconnect(); await StopServer(); }
    private void Changed() { foreach (var p in new[] { nameof(Running), nameof(CanSelectProtocol), nameof(CanConfigure), nameof(CanConnect), nameof(IsConnected), nameof(ConnectionState), nameof(CanConfigureServer), nameof(ServerToggleText), nameof(PollText) }) Raise(p); }
    private void SaveMemory() { var d = new SaveFileDialog { Filter = "PLC 内存快照|*.json", FileName = $"LinkLab-{Protocol}-memory.json" }; if (d.ShowDialog() == true) File.WriteAllText(d.FileName, Memory.Snapshot()); }
    private void LoadMemory() { var d = new OpenFileDialog { Filter = "PLC 内存快照|*.json" }; if (d.ShowDialog() == true) { if (new FileInfo(d.FileName).Length > 12_000_000) throw new IOException("快照超过 12 MB。"); Memory.Restore(File.ReadAllText(d.FileName)); Refresh(); Hub.Notice = "模拟内存快照已载入"; } }
}
