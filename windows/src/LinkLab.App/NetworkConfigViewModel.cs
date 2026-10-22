using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using LinkLab.Core;
using Microsoft.Win32;

namespace LinkLab.App;

public sealed class NetworkConfigVm : PageVm
{
    private readonly INetworkConfigurationBackend _backend;
    private readonly CancellationTokenSource _stop = new();
    private Task _operation = Task.CompletedTask;
    private bool _busy, _closing, _hasError;
    private string _status = "选择本机网卡后搜索设备，或输入 IP 定向读取。", _log = "尚未发送网络报文。", _preview = "", _bindAddress = "0.0.0.0";
    private NetworkDeviceSnapshot? _selected;
    private NetworkDeviceSettings? _reviewed;
    private string _name = "", _address = "192.168.1.200", _mask = "255.255.255.0", _gateway = "192.168.1.1", _localPort = "4196", _destination = "192.168.1.100", _destinationPort = "4196";
    private int _mode, _baud = 9600, _parity, _dataBits = 8, _stopBits = 1, _flowControl, _appProtocol;
    private bool _dhcp;
    [Saved] public string BindAddress { get => _bindAddress; set => Set(ref _bindAddress, Interfaces.Any(nic => nic.Address == value) ? value : "0.0.0.0"); }
    [Saved] public string Target { get; set; } = "192.168.1.200";
    [Saved] public string Timeout { get; set; } = "2000";
    public IReadOnlyList<NetworkInterfaceChoice> Interfaces { get; private set; } = NetworkConfiguration.Interfaces();
    public string[] Modes => NetworkConfiguration.Modes;
    public int[] BaudRates => NetworkConfiguration.BaudRates;
    public string[] Parities => NetworkConfiguration.Parities;
    public string[] AppProtocols => NetworkConfiguration.AppProtocols;
    public int[] DataBitChoices { get; } = [5, 6, 7, 8];
    public int[] StopBitChoices { get; } = [1, 2];
    public string[] FlowControls { get; } = ["None", "CTS / RTS", "DTR / DSR", "XON / XOFF"];
    public bool Busy { get => _busy; private set { if (Set(ref _busy, value)) Changed(); } }
    public bool Idle => !Busy && !_closing;
    public bool CanRead => Idle && Selected is not null;
    public bool CanEdit => Idle && Selected is { Writable: true };
    public bool CanRestart => CanEdit;
    public bool CanApply => CanEdit && _reviewed is not null;
    public string Password { get; set; } = "";
    public bool PasswordRequired => Selected?.PasswordRequired == true;
    public bool CanUseDns => Selected?.SupportsDns == true;
    public bool CanUseDhcp => Selected?.SupportsDhcp == true;
    public bool HasError { get => _hasError; private set => Set(ref _hasError, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public string Log { get => _log; private set => Set(ref _log, value); }
    public string Preview { get => _preview; private set { Set(ref _preview, value); Raise(nameof(HasPreview)); } }
    public bool HasPreview => Preview.Length != 0;
    public string CountText => $"{Devices.Count} 台设备 · 按 DevID / MAC 区分";
    public ObservableCollection<NetworkDeviceSnapshot> Devices { get; } = [];
    public NetworkDeviceSnapshot? Selected
    {
        get => _selected;
        set { if (Set(ref _selected, value)) { Password = ""; Raise(nameof(Password)); if (value is not null) Load(value.Settings); else InvalidatePreview(); Changed(); } }
    }
    public string SelectedLabel => Selected is null ? "选择设备后读取与编辑" : $"{Selected.Identity}  ·  原 IP {Selected.Address}";
    public string Name { get => _name; set { if (Set(ref _name, value)) InvalidatePreview(); } }
    public string Address { get => _address; set { if (Set(ref _address, value)) InvalidatePreview(); } }
    public string Mask { get => _mask; set { if (Set(ref _mask, value)) InvalidatePreview(); } }
    public string Gateway { get => _gateway; set { if (Set(ref _gateway, value)) InvalidatePreview(); } }
    public string LocalPort { get => _localPort; set { if (Set(ref _localPort, value)) InvalidatePreview(); } }
    public string Destination { get => _destination; set { if (Set(ref _destination, value)) InvalidatePreview(); } }
    public string DestinationPort { get => _destinationPort; set { if (Set(ref _destinationPort, value)) InvalidatePreview(); } }
    public int Mode { get => _mode; set { if (Set(ref _mode, value)) InvalidatePreview(); } }
    public int Baud { get => _baud; set { if (Set(ref _baud, value)) InvalidatePreview(); } }
    public int Parity { get => _parity; set { if (Set(ref _parity, value)) InvalidatePreview(); } }
    public bool Dhcp { get => _dhcp; set { if (Set(ref _dhcp, value)) InvalidatePreview(); } }
    public int DataBits { get => _dataBits; set { if (Set(ref _dataBits, value)) InvalidatePreview(); } }
    public int StopBits { get => _stopBits; set { if (Set(ref _stopBits, value)) InvalidatePreview(); } }
    public int FlowControl { get => _flowControl; set { if (Set(ref _flowControl, value)) InvalidatePreview(); } }
    public int AppProtocol { get => _appProtocol; set { if (Set(ref _appProtocol, value)) InvalidatePreview(); } }
    public AsyncCommand SearchCommand { get; }
    public AsyncCommand QueryCommand { get; }
    public AsyncCommand ReadCommand { get; }
    public AsyncCommand ApplyCommand { get; }
    public AsyncCommand RestartCommand { get; }
    public Command RefreshInterfacesCommand { get; }
    public Command ReviewCommand { get; }
    public Command RestoreCommand { get; }
    public Command ExportCommand { get; }
    public Command ImportCommand { get; }
    public Command ClearLogCommand { get; }

    public NetworkConfigVm(Hub hub, INetworkConfigurationBackend? backend = null) : base(hub)
    {
        _backend = backend ?? new ZlanNetworkConfigurationBackend();
        _backend.Traffic += Hub.Record;
        SearchCommand = Action(() => Search(true)); QueryCommand = Action(() => Search(false));
        ReadCommand = Action(Read); ApplyCommand = Action(Apply); RestartCommand = Action(Restart);
        RefreshInterfacesCommand = Safe(() => { Interfaces = NetworkConfiguration.Interfaces(); Raise(nameof(Interfaces)); if (!Interfaces.Any(nic => nic.Address == BindAddress)) { BindAddress = "0.0.0.0"; Raise(nameof(BindAddress)); } });
        ReviewCommand = Safe(Review); RestoreCommand = Safe(() => { if (Selected is not null) Load(Selected.Settings); });
        ExportCommand = Safe(Export); ImportCommand = Safe(Import); ClearLogCommand = Safe(() => Log = "日志已清空。");
    }
    private void Changed() { Raise(nameof(Idle)); Raise(nameof(CanRead)); Raise(nameof(CanEdit)); Raise(nameof(CanRestart)); Raise(nameof(CanApply)); Raise(nameof(SelectedLabel)); Raise(nameof(CountText)); Raise(nameof(PasswordRequired)); Raise(nameof(CanUseDns)); Raise(nameof(CanUseDhcp)); }
    private void InvalidatePreview() { _reviewed = null; Preview = ""; Raise(nameof(CanApply)); }
    private Command Safe(Action action) => new(() => { try { if (Idle) { HasError = false; action(); } } catch (Exception ex) { Error(ex); } });
    private AsyncCommand Action(Func<Task> action) => new(() => Run(action), Error);
    private Task Run(Func<Task> action) { if (!Idle) return Task.CompletedTask; HasError = false; Busy = true; return _operation = RunCore(action); }
    private async Task RunCore(Func<Task> action) { try { await action(); } finally { Busy = false; } }
    private void Error(Exception ex) { HasError = ex is not OperationCanceledException; Status = ex is OperationCanceledException ? "操作已取消。" : ex.Message; Append(Status); Hub.Error(ex); }
    private void Append(string text) { Log += $"\n[{DateTime.Now:HH:mm:ss}] {text}"; if (Log.Length > 24000) Log = Log[^20000..]; }
    private int WaitTime() => Int(Timeout, 200, 15000, "查询超时 / ms");

    public async Task Search(bool broadcast)
    {
        int timeout = WaitTime(); NetworkConfiguration.Ipv4(BindAddress, "本机网卡", true);
        string target = broadcast ? "255.255.255.255" : NetworkConfiguration.Ipv4(Target, "查询 IP").ToString();
        Status = broadcast ? $"正在向 {target}:1092 搜索卓岚兼容设备…" : $"正在读取 {target}:1092…";
        var devices = await _backend.Discover(BindAddress, target, broadcast, timeout, _stop.Token);
        if (broadcast) { Devices.Clear(); Selected = null; }
        foreach (var device in devices) Upsert(device);
        if (devices.Count > 0) Selected = Devices.First(device => device.Identity == devices[0].Identity);
        Status = devices.Count == 0 ? "未收到兼容设备应答。请检查所选网卡、设备供电、网络连接与 UDP 1092 通信。" : $"已收到 {devices.Count} 台设备的完整参数应答。";
        if (devices.Any(device => !device.Writable)) Status += " 非 167 字节参数格式的设备仅支持读取。";
        Append(Status); Changed();
    }
    public async Task Read()
    {
        var selected = Selected ?? throw new InvalidOperationException("请先选择设备。");
        Status = $"正在重读 {selected.Identity} / {selected.Address}…";
        var result = await _backend.Read(BindAddress, selected, WaitTime(), _stop.Token);
        Upsert(result); Selected = Devices.First(device => device.Identity == result.Identity); Status = "已重读设备参数。"; Append(Status);
    }
    public async Task Restart()
    {
        var selected = Selected ?? throw new InvalidOperationException("请先选择设备。");
        _ = ZlanConfigurationProtocol.Restart(selected, Password);
        if (MessageBox.Show($"设备 {selected.Identity}\n当前 IP {selected.Address}\n\n重启将暂时中断设备通信，并保留设备当前参数。编辑区尚未写入的修改不会发送。\n是否重启此设备？", "确认重启所选设备", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        await RestartConfirmed(selected);
    }
    internal async Task RestartConfirmed(NetworkDeviceSnapshot selected)
    {
        Status = $"正在确认 {selected.Identity} 的身份并发送重启指令，等待设备回读…";
        var result = await _backend.Restart(BindAddress, selected, Password, WaitTime(), _stop.Token);
        Upsert(result); Selected = Devices.First(device => device.Identity == result.Identity);
        Load(result.Settings);
        Status = "重启指令已发送：已重新读到同一 DevID 的设备，参数一致。"; Append(Status);
    }
    private void Upsert(NetworkDeviceSnapshot value)
    {
        var old = Devices.FirstOrDefault(device => device.Identity == value.Identity);
        if (old is null) Devices.Add(value); else Devices[Devices.IndexOf(old)] = value;
        Changed();
    }
    private void Load(NetworkDeviceSettings value)
    {
        Name = value.Name; Address = value.Address; Mask = value.Mask; Gateway = value.Gateway; LocalPort = value.LocalPort.ToString();
        Destination = value.Destination; DestinationPort = value.DestinationPort.ToString(); Mode = value.Mode; Baud = value.Baud; Parity = value.Parity;
        Dhcp = value.Dhcp; DataBits = value.DataBits; StopBits = value.StopBits; FlowControl = value.FlowControl; AppProtocol = value.AppProtocol; InvalidatePreview();
    }
    public NetworkDeviceSettings Settings()
    {
        var settings = new NetworkDeviceSettings(Name.Trim(), Address.Trim(), Mask.Trim(), Gateway.Trim(), Int(LocalPort, Mode == 1 ? 0 : 1, 65535, "本地端口"), Destination.Trim(), Int(DestinationPort, 1, 65535, "目标端口"), Mode, Baud, Parity, Dhcp, DataBits, StopBits, FlowControl, AppProtocol);
        NetworkConfiguration.Validate(settings); return settings;
    }
    public void Review()
    {
        var selected = Selected ?? throw new InvalidOperationException("请先搜索并选择设备，保留设备原始参数后才能修改。");
        if (!selected.Writable) throw new InvalidOperationException("当前固件参数格式仅支持读取，不能写入。");
        var candidate = Settings();
        _ = ZlanConfigurationProtocol.Write(selected, candidate, Password);
        _reviewed = candidate;
        var value = _reviewed;
        Preview = $"设备 {selected.Identity}  ·  当前 IP {selected.Address}\n名称 {value.Name}  ·  新 IP {value.Address} / {value.Mask}\n网关 {value.Gateway}  ·  本地端口 {(value.LocalPort == 0 ? "0（随机）" : value.LocalPort.ToString())}  ·  {(value.Dhcp ? "DHCP" : "静态 IP")}\n{Modes[value.Mode]} → {value.Destination}:{value.DestinationPort}\n串口 {value.Baud} / {value.DataBits}{Parities[value.Parity]}{value.StopBits}  ·  流控 {FlowControls[value.FlowControl]}\n转化协议 {AppProtocols[value.AppProtocol]}\n写入后设备会重启；仅在重新读到同一设备并核对参数后显示成功。";
        Raise(nameof(CanApply)); Status = "请核对目标设备与参数，再点击确认写入。";
    }
    public async Task Apply()
    {
        var selected = Selected ?? throw new InvalidOperationException("请选择设备。");
        var value = _reviewed ?? throw new InvalidOperationException("请先检查修改，确认目标与参数。");
        if (value != Settings()) { InvalidatePreview(); throw new InvalidOperationException("参数已变化，请重新检查修改。"); }
        if (MessageBox.Show($"{Preview}\n\n是否将这些参数写入此设备？", "确认设备参数写入", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        await ApplyReviewed(selected, value);
    }
    internal async Task ApplyReviewed(NetworkDeviceSnapshot selected, NetworkDeviceSettings value)
    {
        Status = "正在重新确认设备身份、写入并等待设备重启后的参数回读…";
        var result = await _backend.Apply(BindAddress, selected, value, Password, WaitTime(), _stop.Token);
        Upsert(result); Selected = Devices.First(device => device.Identity == result.Identity);
        Status = "写入成功：已重新读到同一 DevID 的设备，配置参数一致。"; Append(Status);
    }
    private sealed record Profile(int FormatVersion, NetworkDeviceSettings Settings);
    private void Export()
    {
        var dialog = new SaveFileDialog { Filter = "设备联调工具网口参数模板|*.json", FileName = "设备联调工具-网口参数.json" };
        var settings = Settings(); if (dialog.ShowDialog() != true) return;
        File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(new Profile(1, settings), new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
        Status = "已导出参数模板，不包含设备身份、原始报文或密码。";
    }
    private void Import()
    {
        if (Selected is null) throw new InvalidOperationException("请先选择目标设备，再载入参数模板。");
        var dialog = new OpenFileDialog { Filter = "设备联调工具网口参数模板|*.json" }; if (dialog.ShowDialog() != true) return;
        if (new FileInfo(dialog.FileName).Length > 65536) throw new IOException("参数模板文件过大。");
        var profile = JsonSerializer.Deserialize<Profile>(File.ReadAllText(dialog.FileName));
        if (profile is null || profile.FormatVersion != 1 || profile.Settings is null) throw new IOException("参数模板格式不受支持。");
        NetworkConfiguration.Validate(profile.Settings); Load(profile.Settings); Status = "模板已载入编辑区，尚未写入设备。";
    }
    public async Task Close()
    {
        if (_closing) { try { await _operation; } catch (Exception) { } return; }
        _closing = true; _stop.Cancel(); Changed();
        // AsyncCommand already reports operation failures. Shutdown must still
        // complete if a query failed while the main window was closing.
        try { await _operation; } catch (Exception) { }
        _backend.Traffic -= Hub.Record;
    }
}
