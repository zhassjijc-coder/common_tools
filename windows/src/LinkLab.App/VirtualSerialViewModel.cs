using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Windows;
using LinkLab.Core;
using Microsoft.Win32;

namespace LinkLab.App;

public sealed record VirtualPairRow(VirtualPortPair Pair, bool Present, string? Problem = null)
{
    public int Id => Pair.Id;
    public string PortA => Pair.PortA;
    public string PortB => Pair.PortB;
    public string Status => Present ? "系统已枚举 · 可测试" : Problem ?? "未就绪 / 非 COM 名称";
}

public sealed class VirtualSerialVm : PageVm
{
    private readonly ShellViewModel _shell;
    private readonly VirtualPortManager _manager;
    private readonly CancellationTokenSource _stop = new();
    private Task _operation = Task.CompletedTask;
    private bool _busy, _closing, _hasError, _portsLoaded, _updatingPorts;
    private string[] _freePorts = [];
    private string _toolPath = VirtualSerialBackend.DetectTool(), _portA = "COM3", _portB = "COM4", _driverBlock = "", _status = "安装驱动后点击刷新，或选择已有的 setupc.exe。", _log = "尚未执行测试。";
    private VirtualPairRow? _selected;
    [Saved] public string ToolPath { get => _toolPath; set { if (Set(ref _toolPath, value)) { Pairs.Clear(); Selected = null; Status = "驱动工具路径已变化，请刷新检测。"; Raise(nameof(DriverHint)); Raise(nameof(CanManage)); Raise(nameof(CanCreate)); } } }
    [Saved] public string PortA { get => _portA; set { if (!_updatingPorts && value is not null && Set(ref _portA, value)) UpdatePortChoices(); } }
    [Saved] public string PortB { get => _portB; set { if (!_updatingPorts && value is not null && Set(ref _portB, value)) UpdatePortChoices(); } }
    [Saved] public string Baud { get; set; } = "9600";
    [Saved] public string Unit { get; set; } = "1";
    public string DriverHint => DriverBlocked ? "驱动已被 Windows 阻止 · 管理员运行不能解除驱动拦截" : File.Exists(ToolPath) ? "已找到管理工具 · 刷新确认驱动与端口状态" : "需要 com0com 驱动 · 当前未找到管理工具";
    public bool DriverBlocked => _driverBlock.Length > 0;
    public string DriverRecoveryHint => DriverBlocked ? _driverBlock + " 这是内核驱动兼容性或签名问题，管理员运行也无法解决。请安装受当前 Windows 安全策略接受的驱动后刷新检测；现有未就绪端口对仍可删除。" : "";
    public bool Busy { get => _busy; private set { Set(ref _busy, value); Raise(nameof(Idle)); Raise(nameof(CanManage)); Raise(nameof(CanCreate)); Raise(nameof(CanUsePair)); Raise(nameof(CanDeletePair)); Raise(nameof(CreateLabel)); } }
    public string CreateLabel => Busy ? "处理中…" : "创建端口对";
    public bool HasError { get => _hasError; private set => Set(ref _hasError, value); }
    public bool Idle => !Busy && !_closing;
    public bool CanManage => Idle && File.Exists(ToolPath);
    private bool HasPortSelection => _portsLoaded && _freePorts.Contains(PortA) && _freePorts.Contains(PortB) && PortA != PortB;
    public bool CanCreate => CanManage && HasPortSelection && !DriverBlocked;
    public string[] PortAChoices { get; private set; } = [];
    public string[] PortBChoices { get; private set; } = [];
    public string PortChoiceHint => !_portsLoaded ? "尚未获得可用 COM 号，请刷新检测；检测完成前不能创建。" : _freePorts.Length < 2
        ? "可用 COM 号不足两个，暂时无法创建端口对。请检查设备后刷新。"
        : "仅显示未占用的 COM 号，已过滤蓝牙等现有设备、Windows 保留名称及已有端口对；两端不可重复。";
    public bool CanUsePair => CanManage && Selected is { Present: true };
    public bool CanDeletePair => CanManage && Selected is not null;
    public string Status { get => _status; private set => Set(ref _status, value); }
    public string Log { get => _log; private set => Set(ref _log, value); }
    public ObservableCollection<VirtualPairRow> Pairs { get; } = [];
    public VirtualPairRow? Selected { get => _selected; set { Set(ref _selected, value); Raise(nameof(SelectedLabel)); Raise(nameof(CanUsePair)); Raise(nameof(CanDeletePair)); } }
    public string SelectedLabel => Selected?.Pair.Label ?? "请先创建或选择端口对";
    public AsyncCommand RefreshCommand { get; }
    public AsyncCommand SuggestCommand { get; }
    public AsyncCommand BrowseCommand { get; }
    public AsyncCommand CreateCommand { get; }
    public AsyncCommand DeleteCommand { get; }
    public AsyncCommand DuplexCommand { get; }
    public AsyncCommand RtuCommand { get; }
    public AsyncCommand ConfigureCommand { get; }
    public Command DownloadCommand { get; }
    public Command MasterCommand { get; }
    public Command SimulatorCommand { get; }
    public VirtualSerialVm(ShellViewModel shell, IVirtualPortBackend? backend = null) : base(shell.Hub)
    {
        _shell = shell; _manager = new(backend ?? new VirtualSerialBackend(() => ToolPath));
        RefreshCommand = Action(Refresh);
        SuggestCommand = Action(() => RefreshPorts(true));
        BrowseCommand = Action(async () => { var dialog = new OpenFileDialog { Title = "选择 com0com 安装目录中的 setupc.exe", Filter = "com0com 管理工具 (setupc.exe)|setupc.exe", CheckFileExists = true }; if (dialog.ShowDialog() == true) { ToolPath = dialog.FileName; await Refresh(); } });
        CreateCommand = Action(Create);
        DeleteCommand = Action(Delete);
        DuplexCommand = Action(() => Test(false)); RtuCommand = Action(() => Test(true));
        ConfigureCommand = Action(ConfigureRtu);
        DownloadCommand = new(() => { try { Process.Start(new ProcessStartInfo("https://sourceforge.net/projects/com0com/files/com0com/") { UseShellExecute = true }); } catch (Exception ex) { Hub.Error(ex); } });
        MasterCommand = new(() => shell.Navigate("master")); SimulatorCommand = new(() => shell.Navigate("simulator"));
    }

    private AsyncCommand Action(Func<Task> action) => new(() => Run(action), ex => { HasError = true; Status = ex is OperationCanceledException && _closing ? "操作已取消。" : ex.Message; Append(Status); Hub.Error(ex); });
    private Task Run(Func<Task> action)
    {
        if (Busy || _closing) return Task.CompletedTask;
        HasError = false; Busy = true;
        return _operation = RunCore(action);
    }
    private async Task RunCore(Func<Task> action) { try { await action(); } finally { Busy = false; } }
    private void Append(string text) { Log = (Log + $"\n[{DateTime.Now:HH:mm:ss}] {text}"); if (Log.Length > 16000) Log = Log[^12000..]; }

    private void UpdatePortChoices()
    {
        // Replacing ItemsSource can temporarily clear SelectedItem. Keep both selections
        // stable while publishing the mutually exclusive lists, then restore the bindings.
        _updatingPorts = true;
        try
        {
            PortAChoices = _freePorts.Where(port => port != PortB).ToArray();
            PortBChoices = _freePorts.Where(port => port != PortA).ToArray();
            Raise(nameof(PortAChoices)); Raise(nameof(PortBChoices));
            Raise(nameof(PortA)); Raise(nameof(PortB)); Raise(nameof(CanCreate)); Raise(nameof(PortChoiceHint));
        }
        finally { _updatingPorts = false; }
    }
    private void SelectFreePorts(VirtualPortSnapshot snapshot, bool suggest)
    {
        var unavailable = snapshot.UnavailableNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        _freePorts = Enumerable.Range(1, 4096).Select(n => $"COM{n}").Where(port => !unavailable.Contains(port)).ToArray();
        // Prefer COM3 upwards, while still offering COM1 and COM2 if they are free.
        var preferred = _freePorts.Where(port => port is not "COM1" and not "COM2").Concat(_freePorts.Where(port => port is "COM1" or "COM2")).ToArray();
        string a = !suggest && _freePorts.Contains(PortA) ? PortA : "";
        string b = !suggest && _freePorts.Contains(PortB) && PortB != a ? PortB : "";
        _portA = a.Length > 0 ? a : preferred.FirstOrDefault(port => port != b) ?? "";
        _portB = b.Length > 0 ? b : preferred.FirstOrDefault(port => port != _portA) ?? "";
        _portsLoaded = true;
        UpdatePortChoices();
    }
    public Task Refresh() => RefreshPorts(false);
    private async Task RefreshPorts(bool suggest)
    {
        if (string.IsNullOrWhiteSpace(ToolPath)) ToolPath = VirtualSerialBackend.DetectTool();
        var previous = Selected?.Pair;
        // Clear stale rows even when a query fails: never present old ports as ready.
        Pairs.Clear(); Selected = null;
        _portsLoaded = false; _freePorts = []; UpdatePortChoices();
        Status = "正在检测现有设备、保留名称和可用 COM 号…";
        var snapshot = await _manager.Refresh(_stop.Token);
        _driverBlock = snapshot.DeviceIssues.Values.FirstOrDefault(issue => issue.Code is 48 or 52)?.Message ?? "";
        Raise(nameof(DriverBlocked)); Raise(nameof(DriverHint)); Raise(nameof(DriverRecoveryHint)); Raise(nameof(CanCreate));
        SelectFreePorts(snapshot, suggest);
        foreach (var pair in snapshot.Pairs) Pairs.Add(new(pair, snapshot.IsPresent(pair), snapshot.DeviceIssues.GetValueOrDefault(pair.Id)?.Message));
        Selected = Pairs.FirstOrDefault(p => p.Pair == previous) ?? Pairs.FirstOrDefault();
        Status = $"检测到 {Pairs.Count} 对 com0com 端口，Windows 当前枚举 {snapshot.PresentPorts.Length} 个串口。";
        HasError = snapshot.DeviceIssues.Count > 0;
        if (HasError) Status += " " + string.Join(" ", snapshot.DeviceIssues.Values.Select(issue => issue.Message).Distinct());
        if (string.IsNullOrWhiteSpace(ToolPath)) Status += " 请先安装驱动或选择工具路径。";
        if (_freePorts.Length < 2) Status += " " + PortChoiceHint;
        else if (suggest) Status += $" 已选择 {PortA} ↔ {PortB}，创建前会再次检查占用。";
    }
    private async Task Create()
    {
        if (!HasPortSelection) throw new InvalidOperationException("请先刷新检测，并从下拉框选择两个不同的可用 COM 号。");
        if (DriverBlocked) throw new IOException(DriverRecoveryHint);
        string a = VirtualSerial.PortName(PortA), b = VirtualSerial.PortName(PortB);
        Status = "正在创建端口对，请完成 Windows 管理员授权；驱动操作结束前请勿关闭程序。";
        try
        {
            var pair = await _manager.Create(a, b, _stop.Token);
            await Refresh(); Selected = Pairs.FirstOrDefault(p => p.Pair == pair);
            Status = $"已创建 {pair.Label}，两个端口均已被 Windows 枚举。"; Append(Status);
        }
        catch
        {
            // A driver may have created an unready device. Show it instead of hiding partial results.
            try { await Refresh(); } catch { Pairs.Clear(); Selected = null; }
            throw;
        }
    }
    private async Task Delete()
    {
        var pair = Selected?.Pair ?? throw new InvalidOperationException("请先选择要删除的端口对。");
        if (MessageBox.Show(Application.Current.MainWindow, $"删除 {pair.Label}（编号 {pair.Id}）？\n请先关闭所有使用这两个端口的连接。此操作会从 Windows 移除这一对虚拟设备。", "删除虚拟串口", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
        Status = $"正在删除 {pair.Label}，请完成 Windows 管理员授权…";
        try { await _manager.Remove(pair, _stop.Token); await Refresh(); Status = $"已删除 {pair.Label}。"; Append(Status); }
        catch { Pairs.Clear(); Selected = null; throw; }
    }
    private async Task<VirtualPortPair> CurrentPair()
    {
        var pair = Selected?.Pair ?? throw new InvalidOperationException("请先选择端口对。");
        return VirtualSerial.RequirePresent(pair, await _manager.Refresh(_stop.Token));
    }

    public async Task ConfigureRtu()
    {
        var pair = await CurrentPair();
        int baud = Int(Baud, 1200, 115200, "波特率"); byte unit = (byte)Int(Unit, 1, 247, "站号");
        if (_shell.Simulator.Running || _shell.Simulator.ToggleCommand.CanExecute(null) == false || _shell.Master.HasSession || !_shell.Master.ExecuteCommand.CanExecute(null))
            throw new InvalidOperationException("请先停止设备模拟，并在 Modbus 主站中断开连接、停止轮询，再应用 RTU 参数。");
        _shell.Master.Mode = 1; _shell.Simulator.Mode = 1;
        Fill(_shell.Master.Serial, pair.PortA, baud); Fill(_shell.Simulator.Serial, pair.PortB, baud);
        _shell.Master.Query.Unit = unit.ToString(); _shell.Master.Query.FunctionIndex = 2; _shell.Master.Query.AddressMode = 0; _shell.Master.Query.Address = "0"; _shell.Master.Query.Quantity = "3"; _shell.Master.Query.Raise("");
        _shell.Master.Timeout = "2000"; _shell.Master.DataType = "UInt16"; _shell.Master.Raise("");
        _shell.Simulator.Unit = unit.ToString(); _shell.Simulator.Raise("");
        Status = $"已配置：主站 {pair.PortA} → 从站 {pair.PortB}，{baud} / 8N1，站号 {unit}。先启动设备模拟，再到主站执行读取。";
        Append(Status); Hub.Notice = Status;
    }
    private static void Fill(SerialConfigVm config, string port, int baud)
    {
        config.RefreshCommand.Execute(null); config.Port = port; config.Baud = baud.ToString(); config.Bits = 8; config.Parity = Parity.None; config.Stop = StopBits.One; config.Rts = false; config.Dtr = false; config.Raise("");
    }

    private async Task Test(bool rtu)
    {
        var pair = await CurrentPair();
        int baud = Int(Baud, 1200, 115200, "波特率"); byte unit = (byte)Int(Unit, 1, 247, "站号");
        Status = $"正在测试 {pair.Label}，请保持两个端口空闲…"; Append(Status);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token); deadline.CancelAfter(TimeSpan.FromSeconds(12));
        try
        {
            if (rtu) await RtuProbe(pair, baud, unit, deadline.Token);
            else await Task.Run(() => DuplexProbe(pair, baud, deadline.Token), deadline.Token);
        }
        catch (OperationCanceledException) when (!_stop.IsCancellationRequested) { throw new TimeoutException("测试在 12 秒内未完成，请检查端口状态及参数后重试。端口已释放。"); }
        Status = rtu ? "RTU 自检通过：FC03 读取、FC06 写入与回读均一致。两个端口已释放。" : "双向收发测试通过：A → B、B → A 字节一致。两个端口已释放。";
        Append(Status); Hub.Notice = Status;
    }
    private void DuplexProbe(VirtualPortPair pair, int baud, CancellationToken ct)
    {
        using var a = new SerialSettings(pair.PortA, baud).Open(); using var b = new SerialSettings(pair.PortB, baud).Open();
        a.DiscardInBuffer(); b.DiscardInBuffer();
        void Transfer(SerialPort from, SerialPort to, byte[] bytes)
        {
            ct.ThrowIfCancellationRequested(); from.Write(bytes, 0, bytes.Length); Hub.Record(new(DateTimeOffset.Now, "虚拟串口自检", "TX", bytes, from.PortName));
            var received = new byte[bytes.Length]; int offset = 0;
            while (offset < received.Length) { ct.ThrowIfCancellationRequested(); try { offset += to.Read(received, offset, received.Length - offset); } catch (TimeoutException) { } }
            Hub.Record(new(DateTimeOffset.Now, "虚拟串口自检", "RX", received, to.PortName));
            if (!bytes.SequenceEqual(received)) throw new IOException("双向测试字节不一致。");
        }
        Transfer(a, b, [0x4C, 0x69, 0x6E, 0x6B, 0x00, 0xFF, 0x0D, 0x0A]); Transfer(b, a, [0x52, 0x54, 0x55, 0xAA, 0x55, 0x01]);
    }
    private async Task RtuProbe(VirtualPortPair pair, int baud, byte unit, CancellationToken ct)
    {
        var memory = new ModbusMemory(); memory.Set(DataArea.HoldingRegisters, 0, 2300); memory.Set(DataArea.HoldingRegisters, 1, 1234); memory.Set(DataArea.HoldingRegisters, 2, 5678);
        await using var server = new ModbusRtuSimulator(memory, unit, new(pair.PortB, baud)); server.Traffic += Hub.Record;
        await Task.Run(() => server.StartAsync(ct), ct);
        await using var master = new ModbusRtuConnection(new(pair.PortA, baud)); master.Traffic += Hub.Record;
        var initial = await master.ExecuteAsync(new(unit, 3, 0, 3, []), 2500, ct);
        if (!initial.SequenceEqual(new ushort[] { 2300, 1234, 5678 })) throw new IOException("RTU 初始寄存器数据不一致。");
        await master.ExecuteAsync(new(unit, 6, 0, 1, [4321]), 2500, ct);
        var readback = await master.ExecuteAsync(new(unit, 3, 0, 1, []), 2500, ct);
        if (readback.Length != 1 || readback[0] != 4321 || memory.Get(DataArea.HoldingRegisters, 0) != 4321) throw new IOException("RTU 写入回读不一致。");
    }
    public async Task Close()
    {
        _closing = true; _stop.Cancel();
        try { await _operation; } catch (Exception) { /* Command reports the original failure. */ }
    }
}
