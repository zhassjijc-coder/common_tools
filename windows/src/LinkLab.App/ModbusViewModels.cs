using System.Collections.ObjectModel;
using System.Diagnostics;
using LinkLab.Core;

namespace LinkLab.App;

public sealed class QueryVm : Observable
{
    private int _function = 2, _addressMode;
    private string _address = "0", _quantity = "10";
    [Saved] public int FunctionIndex { get => _function; set { if (Set(ref _function, value)) { if (value is 4 or 5) Quantity = "1"; Raise(nameof(ActualAddress)); Raise(nameof(IsWrite)); } } }
    [Saved] public int AddressMode { get => _addressMode; set { Set(ref _addressMode, value); Raise(nameof(ActualAddress)); } }
    [Saved] public string Unit { get; set; } = "1";
    [Saved] public string Address { get => _address; set { Set(ref _address, value); Raise(nameof(ActualAddress)); } }
    [Saved] public string Quantity { get => _quantity; set => Set(ref _quantity, value); }
    [Saved] public string Values { get; set; } = "100";
    public string[] Functions => ModbusProtocol.Functions;
    public string[] AddressModes { get; } = ["协议地址 · 从 0 开始", "引用地址 · 40001 / 30001 / 10001 / 00001"];
    public bool IsWrite => FunctionIndex >= 4;
    public string ActualAddress
    {
        get { try { return $"实际地址：{ModbusProtocol.Address(ByteTools.Number(Address), AddressMode == 1, ModbusProtocol.FunctionCodes[FunctionIndex])}（十进制）"; } catch { return "请检查地址与地址模式"; } }
    }
    public ModbusRequest Build()
    {
        int unit = ByteTools.Number(Unit), q = ByteTools.Number(Quantity);
        if (unit is < 0 or > 255) throw new ArgumentException("站号范围为 0–255；RTU 交互使用 1–247。");
        if (q is < 1 or > 2000) throw new ArgumentException("数量范围为 1–2000，具体上限由功能码决定。");
        byte fc = ModbusProtocol.FunctionCodes[FunctionIndex];
        ushort addr = ModbusProtocol.Address(ByteTools.Number(Address), AddressMode == 1, fc);
        ushort[] values = IsWrite ? Values.Split([',', ' ', '\r', '\n', ';', '，'], StringSplitOptions.RemoveEmptyEntries).Select(v =>
        {
            int n = ByteTools.Number(v); if (n is < 0 or > 65535) throw new ArgumentException("写入值范围为 0–65535，支持 0x 十六进制。"); return (ushort)n;
        }).ToArray() : [];
        var request = new ModbusRequest((byte)unit, fc, addr, (ushort)q, values);
        _ = request.Pdu(); return request;
    }
}

public sealed record RegisterRow(int Address, string Reference, string Raw, string Hex, string Value);

public sealed class MasterVm : PageVm
{
    private IModbusConnection? _connection;
    private string _connectionKey = "";
    private CancellationTokenSource? _poll;
    private Task? _pollTask;
    private CancellationTokenSource? _requestStop;
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private int _mode;
    [Saved] public int Mode { get => _mode; set { Set(ref _mode, value); Raise(nameof(IsTcp)); Raise(nameof(IsRtu)); } }
    public bool IsTcp => Mode == 0;
    public bool IsRtu => !IsTcp;
    public string[] Modes { get; } = ["Modbus TCP", "Modbus RTU / RS485"];
    public QueryVm Query { get; } = new();
    public SerialConfigVm Serial { get; } = new();
    [Saved] public string Host { get; set; } = "127.0.0.1";
    [Saved] public string Port { get; set; } = "5020";
    [Saved] public string Timeout { get; set; } = "1500";
    [Saved] public string Interval { get; set; } = "1000";
    private string _type = "UInt16";
    private WordOrder _order;
    [Saved] public string DataType { get => _type; set { Set(ref _type, value); UpdateRows(); } }
    [Saved] public WordOrder Order { get => _order; set { Set(ref _order, value); UpdateRows(); } }
    public string[] DataTypes { get; } = ["UInt16", "Int16", "UInt32", "Int32", "Float32", "HEX"];
    public WordOrder[] Orders { get; } = Enum.GetValues<WordOrder>();
    public ObservableCollection<RegisterRow> Rows { get; } = [];
    private ushort[] _last = [];
    private ModbusRequest? _lastRequest;
    private string _status = "填写参数后执行 · 首次请求自动连接";
    public string Status { get => _status; set => Set(ref _status, value); }
    public string PollText => _poll is null ? "开始轮询" : "停止轮询";
    public bool CanConfigure => _poll is null;
    public bool HasSession => _connection is not null || _poll is not null || _requestGate.CurrentCount == 0;
    public AsyncCommand ExecuteCommand { get; }
    public Command PollCommand { get; }
    public AsyncCommand DisconnectCommand { get; }
    public MasterVm(Hub hub) : base(hub)
    { ExecuteCommand = Async(() => Execute()); PollCommand = new(TogglePoll); DisconnectCommand = Async(Close); }
    public async Task Execute(CancellationToken ct = default)
    {
        await _requestGate.WaitAsync(ct);
        try
        {
            var request = Query.Build();
            var settings = IsTcp ? null : Serial.Settings();
            string key = IsTcp ? $"TCP/{Host}/{Port}" : $"RTU/{settings}";
            int timeout = Int(Timeout, 100, 60000, "超时");
            if (_connection is null || _connectionKey != key)
            {
                if (_connection is not null) await _connection.DisposeAsync();
                _connection = IsTcp ? new ModbusTcpConnection(Host.Trim(), Int(Port, 1, 65535, "端口")) : new ModbusRtuConnection(settings!);
                _connection.Traffic += Hub.Record; _connectionKey = key;
            }
            _requestStop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var sw = Stopwatch.StartNew(); Status = "正在等待设备响应…";
            _last = await _connection.ExecuteAsync(request, timeout, _requestStop.Token); _lastRequest = request; UpdateRows();
            Status = $"{(request.Function <= 4 ? "读取" : "写入")}成功 · {_last.Length} 个值 · {sw.ElapsedMilliseconds} ms · {DateTime.Now:HH:mm:ss}";
            Hub.Notice = Status;
        }
        catch (Exception ex) { Status = ex is OperationCanceledException ? "操作已停止" : ex.Message; throw; }
        finally { _requestStop?.Dispose(); _requestStop = null; _requestGate.Release(); }
    }
    private void UpdateRows()
    {
        Rows.Clear(); if (_lastRequest is null) return;
        bool bits = _lastRequest.Function is 1 or 2 or 5 or 15;
        int step = !bits && DataType is "UInt32" or "Int32" or "Float32" ? 2 : 1;
        int offset = _lastRequest.Function switch { 1 or 5 or 15 => 1, 2 => 10001, 4 => 30001, _ => 40001 };
        for (int i = 0; i < _last.Length; i += step)
        {
            int address = _lastRequest.Address + i;
            string raw = string.Join(" / ", _last.Skip(i).Take(step));
            string hex = string.Join(" ", _last.Skip(i).Take(step).Select(v => $"{v:X4}"));
            Rows.Add(new(address, (offset + address).ToString("D5"), raw, hex, bits ? (_last[i] != 0 ? "ON · 1" : "OFF · 0") : ByteTools.Decode(_last, i, DataType, Order)));
        }
    }
    private void TogglePoll()
    {
        if (_poll is not null) { _poll.Cancel(); return; }
        try
        {
            if (Query.Build().Function > 4) throw new ArgumentException("轮询仅支持读取功能码；写入请使用执行一次。");
            int ms = Int(Interval, 50, 3600000, "轮询间隔");
            _poll = new(); Raise(nameof(PollText)); Raise(nameof(CanConfigure)); _pollTask = Poll(ms, _poll.Token);
        }
        catch (Exception ex) { Hub.Error(ex); }
    }
    private async Task Poll(int ms, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try { await Execute(ct); } catch (Exception ex) when (ex is not OperationCanceledException) { Hub.Error(ex); }
                await Task.Delay(ms, ct);
            }
        }
        catch (OperationCanceledException) { }
        finally { _poll?.Dispose(); _poll = null; Raise(nameof(PollText)); Raise(nameof(CanConfigure)); }
    }
    public async Task Close()
    {
        _poll?.Cancel(); _requestStop?.Cancel();
        if (_pollTask is not null) await _pollTask;
        await _requestGate.WaitAsync();
        try { if (_connection is not null) await _connection.DisposeAsync(); _connection = null; Status = "连接已释放"; }
        finally { _requestGate.Release(); }
    }
}

public sealed class MemoryRow : Observable
{
    public ushort Address { get; init; }
    private string _value = "0";
    public string Value { get => _value; set => Set(ref _value, value); }
    public string Hex { get; init; } = "0000";
}

public sealed class SimulatorVm : PageVm
{
    public ModbusMemory Memory { get; } = new();
    private IModbusSimulator? _server;
    private int _mode;
    [Saved] public int Mode { get => _mode; set { Set(ref _mode, value); Raise(nameof(IsTcp)); Raise(nameof(IsRtu)); } }
    public bool IsTcp => Mode == 0;
    public bool IsRtu => !IsTcp;
    public string[] Modes { get; } = ["Modbus TCP 服务端", "Modbus RTU 从站"];
    [Saved] public string Bind { get; set; } = "127.0.0.1";
    [Saved] public string Port { get; set; } = "5020";
    [Saved] public string Unit { get; set; } = "1";
    public SerialConfigVm Serial { get; } = new();
    public bool Running => _server is not null;
    public bool CanConfigure => !Running;
    public string ToggleText => Running ? "停止模拟" : "启动模拟";
    private string _status = "内置演示数据 · 与主站默认参数匹配";
    public string Status { get => _status; set => Set(ref _status, value); }
    [Saved] public int Area { get; set; } = 2;
    public string[] Areas { get; } = ["线圈 Coils", "离散输入 Discrete Inputs", "保持寄存器 Holding Registers", "输入寄存器 Input Registers"];
    [Saved] public string Start { get; set; } = "0";
    [Saved] public string Count { get; set; } = "16";
    public ObservableCollection<MemoryRow> Rows { get; } = [];
    public AsyncCommand ToggleCommand { get; }
    public Command RefreshCommand { get; }
    public Command ApplyCommand { get; }
    private DataArea _displayArea = DataArea.HoldingRegisters;
    public SimulatorVm(Hub hub) : base(hub)
    {
        Memory.Set(DataArea.HoldingRegisters, 0, 2300); Memory.Set(DataArea.HoldingRegisters, 1, 1234); Memory.Set(DataArea.HoldingRegisters, 2, 5678);
        ToggleCommand = Async(Toggle);
        RefreshCommand = new(() => { try { Refresh(); } catch (Exception ex) { Hub.Error(ex); } });
        ApplyCommand = new(() => { try { Apply(); } catch (Exception ex) { Hub.Error(ex); } }); Refresh();
    }
    public void Refresh()
    {
        int start = Int(Start, 0, 65535, "起始地址"), count = Int(Count, 1, 256, "展示数量");
        if (start + count > 65536) throw new ArgumentException("地址范围超出 65535。");
        _displayArea = (DataArea)Area; Rows.Clear();
        for (int i = start; i < start + count; i++) { var value = Memory.Get(_displayArea, (ushort)i); Rows.Add(new() { Address = (ushort)i, Value = value.ToString(), Hex = $"0x{value:X4}" }); }
    }
    private void Apply()
    {
        var values = Rows.Select(x => (x.Address, Value: Int(x.Value, 0, _displayArea is DataArea.Coils or DataArea.DiscreteInputs ? 1 : 65535, $"地址 {x.Address} 的值"))).ToArray();
        foreach (var (address, value) in values) Memory.Set(_displayArea, address, (ushort)value);
        Refresh(); Hub.Notice = "数据区已更新，主站现在可以读取新值";
    }
    private async Task Toggle()
    {
        if (Running) { await Close(); return; }
        byte unit = (byte)Int(Unit, IsTcp ? 0 : 1, IsTcp ? 255 : 247, "站号");
        IModbusSimulator server = IsTcp ? new ModbusTcpSimulator(Memory, unit, Bind.Trim(), Int(Port, 1, 65535, "监听端口")) : new ModbusRtuSimulator(Memory, unit, Serial.Settings());
        server.Traffic += Hub.Record;
        try { await Task.Run(() => server.StartAsync()); _server = server; Status = IsTcp ? $"正在监听 {Bind}:{Port} · 站号 {unit}" : $"RTU 从站运行中 · {Serial.Port} · 站号 {unit}"; }
        catch { await server.DisposeAsync(); throw; }
        Changed(); Hub.Notice = Status;
    }
    private void Changed() { Raise(nameof(Running)); Raise(nameof(CanConfigure)); Raise(nameof(ToggleText)); }
    public async Task Close()
    { if (_server is not null) { await _server.DisposeAsync(); _server = null; } Status = "模拟已停止，数据区保留"; Changed(); }
}
