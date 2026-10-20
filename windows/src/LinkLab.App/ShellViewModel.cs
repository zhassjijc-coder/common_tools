using System.Collections.ObjectModel;
using System.Reflection;
using LinkLab.Core;

namespace LinkLab.App;

public sealed record NavigationItem(string Key, string Icon, string Title, string Subtitle, string Accent, string Number);
public sealed record HomeTile(string Key, string Number, string English, string Title, string Description, string Accent, string Geometry);
public sealed class HomeVm(ShellViewModel shell)
{
    public ShellViewModel Shell { get; } = shell;
    public HomeTile[] Tiles { get; } =
    [
        new("plc", "07", "PLC LAB", "PLC 调试", "西门子、三菱、欧姆龙，读写与服务器模拟。", "PlcAccent", "M 20 20 L 80 20 L 80 80 L 20 80 Z M 33 33 L 67 33 L 67 67 L 33 67 Z M 30 0 L 30 20 M 50 0 L 50 20 M 70 0 L 70 20 M 30 80 L 30 100 M 50 80 L 50 100 M 70 80 L 70 100 M 0 30 L 20 30 M 0 50 L 20 50 M 0 70 L 20 70 M 80 30 L 100 30 M 80 50 L 100 50 M 80 70 L 100 70"),
        new("mqtt", "08", "MESSAGING", "MQTT 调试", "发布、订阅与消息时间线，让设备开始对话。", "MqttAccent", "M 10 50 L 50 15 L 90 50 L 50 85 Z M 10 50 L 90 50 M 50 15 L 50 85 M 0 40 L 10 50 L 0 60 M 100 40 L 90 50 L 100 60"),
        new("quick", "09", "SYSTEM SETTINGS", "系统配置", "开机自启动、后台驻留与截图 OCR 设置。", "QuickAccent", "M 15 0 L 15 20 M 15 40 L 15 100 M 5 20 L 25 20 L 25 40 L 5 40 Z M 50 0 L 50 60 M 50 80 L 50 100 M 40 60 L 60 60 L 60 80 L 40 80 Z M 85 0 L 85 35 M 85 55 L 85 100 M 75 35 L 95 35 L 95 55 L 75 55 Z"),
        new("serial", "01", "SERIAL", "串口 / RS485", "从 COM 端口开始，收发、校验、定时发送。", "SerialAccent", "M 50 2 L 94 26 L 94 76 L 50 100 L 6 76 L 6 26 Z M 50 25 L 73 38 L 73 63 L 50 76 L 27 63 L 27 38 Z M 50 2 L 50 25 M 94 26 L 73 38 M 94 76 L 73 63"),
        new("network", "02", "NETWORK", "TCP / UDP", "连接设备，或成为一台可交互的服务器。", "NetworkAccent", "M 50 0 A 50 50 0 1 1 49.9 0 M 50 20 A 30 30 0 1 1 49.9 20 M 50 40 A 10 10 0 1 1 49.9 40 M 0 50 L 100 50 M 50 0 L 50 100"),
        new("netconfig", "11", "DEVICE NETWORK", "网口配置软件", "发现设备，集中管理网络与串口参数。", "NetworkAccent", "M 15 10 L 85 10 L 85 65 L 65 65 L 65 90 L 35 90 L 35 65 L 15 65 Z M 30 20 L 30 45 M 43 20 L 43 45 M 57 20 L 57 45 M 70 20 L 70 45"),
        new("master", "03", "MODBUS", "Modbus 主站", "地址、寄存器、数值，读取写入与持续轮询。", "ProtocolAccent", "M 10 10 L 90 10 L 90 90 L 10 90 Z M 10 36 L 90 36 M 10 63 L 90 63 M 36 10 L 36 90 M 63 10 L 63 90"),
        new("simulator", "04", "SIMULATOR", "设备模拟", "没有硬件，也能验证你的 Modbus 上位机。", "SimAccent", "M 50 0 L 100 50 L 50 100 L 0 50 Z M 50 22 L 78 50 L 50 78 L 22 50 Z M 50 0 L 50 100 M 0 50 L 100 50"),
        new("workbench", "05", "FRAME BUILDER", "报文工作台", "输入地址，构造报文，再把它发送给设备。", "BuilderAccent", "M 15 0 L 15 100 M 45 0 L 45 100 M 75 0 L 75 100 M 0 20 L 100 20 M 0 50 L 100 50 M 0 80 L 100 80"),
        new("monitor", "06", "INSPECT", "监视与工具", "报文时间线、协议解析、校验计算与数据转换。", "InspectAccent", "M 0 65 L 15 65 L 25 25 L 40 90 L 55 0 L 70 65 L 100 65")
    ];
}

public sealed class ShellViewModel : Observable
{
    public Hub Hub { get; } = new();
    public SerialVm Serial { get; }
    public NetworkVm Network { get; }
    public NetworkConfigVm NetworkConfig { get; }
    public MasterVm Master { get; }
    public SimulatorVm Simulator { get; }
    public WorkbenchVm Workbench { get; }
    public MonitorVm Monitor { get; }
    public PlcVm Plc { get; }
    public MqttVm Mqtt { get; }
    public QuickVm Quick { get; }
    public SystemSettingsVm SystemSettings { get; }
    public VirtualSerialVm VirtualSerial { get; }
    public HomeVm Home { get; }
    public ObservableCollection<NavigationItem> Navigation { get; } =
    [
        new("home", "◈", "工作台", "连接设备，从这里开始", "SerialAccent", "00"),
        new("serial", "⬡", "串口 / RS485", "连接硬件，观察每一个字节", "SerialAccent", "01"),
        new("network", "◎", "TCP / UDP", "网络连接与客户端、服务端模拟", "NetworkAccent", "02"),
        new("netconfig", "▤", "网口配置软件", "发现设备，读取并配置网络与串口参数", "NetworkAccent", "11"),
        new("master", "▦", "Modbus 主站", "读取、写入与持续轮询", "ProtocolAccent", "03"),
        new("simulator", "◇", "设备模拟", "构建你的虚拟 Modbus 设备", "SimAccent", "04"),
        new("workbench", "⌘", "报文工作台", "从地址到报文，从报文到设备", "BuilderAccent", "05"),
        new("monitor", "≋", "监视与工具", "记录、解析与检查通信细节", "InspectAccent", "06"),
        new("plc", "▣", "PLC 调试", "西门子、三菱、欧姆龙的读写与模拟", "PlcAccent", "07"),
        new("mqtt", "⋈", "MQTT 调试", "连接 Broker，发布与订阅消息", "MqttAccent", "08"),
        new("virtualserial", "⇄", "虚拟串口", "COM 端口配对与 RTU 本机联调", "VirtualAccent", "10"),
        new("quick", "⚙", "系统配置", "开机自启动、后台驻留与截图偏好", "QuickAccent", "09")
    ];
    public IEnumerable<NavigationItem> ModuleNavigation => Navigation.Where(item => item.Key != "quick");
    // Restore this module only after validating a permanently free, commercially usable
    // driver on supported Windows versions without relaxing system security settings.
    public bool VirtualSerialVisible => false;
    public int ToolCount => Navigation.Count - 1;
    public NavigationItem? SelectedModule { get => IsSettingsSelected ? null : SelectedNav; set { if (value is not null) SelectedNav = value; } }
    public bool IsSettingsSelected => SelectedNav.Key == "quick";
    public string VersionLabel => "LINKLAB  /  " + (typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "1.0");
    private NavigationItem _selected = null!;
    public NavigationItem SelectedNav { get => _selected; set { if (value is not null && Set(ref _selected, value)) { Raise(nameof(CurrentPage)); Raise(nameof(CurrentTitle)); Raise(nameof(CurrentSubtitle)); Raise(nameof(IsSettingsSelected)); Raise(nameof(SelectedModule)); if (value.Key == "virtualserial") VirtualSerial.RefreshCommand.Execute(null); } } }
    public object CurrentPage => SelectedNav.Key switch { "serial" => Serial, "network" => Network, "netconfig" => NetworkConfig, "master" => Master, "simulator" => Simulator, "workbench" => Workbench, "monitor" => Monitor, "plc" => Plc, "mqtt" => Mqtt, "quick" => Quick, "virtualserial" => VirtualSerial, _ => Home };
    public string CurrentTitle => SelectedNav.Title;
    public string CurrentSubtitle => SelectedNav.Subtitle;
    public ParameterCommand NavigateCommand { get; }
    public Command ThemeCommand { get; }
    public Command SaveCommand { get; }
    private readonly Preferences _preferences;
    public ShellViewModel(bool useSettings = true, IStartupRegistration? startup = null)
    {
        _preferences = useSettings ? Preferences.Load() : new();
        _preferences.UpgradeTheme();
        if (!VirtualSerialVisible) Navigation.Remove(Navigation.First(item => item.Key == "virtualserial"));
        Serial = new(Hub); Network = new(Hub); Master = new(Hub); Simulator = new(Hub); Workbench = new(Hub); Monitor = new(Hub); Plc = new(Hub); Mqtt = new(Hub); Quick = new(Hub); Home = new(this);
        NetworkConfig = new(Hub);
        VirtualSerial = new(this);
        SystemSettings = new(Hub, useSettings ? Save : () => { }, startup ?? new WindowsStartupRegistration()); Quick.System = SystemSettings;
        _selected = Navigation[0];
        NavigateCommand = new(p => Navigate(p?.ToString() ?? "home"));
        ThemeCommand = new(() => { _preferences.Light = !_preferences.Light; App.ApplyTheme(_preferences.Light); });
        SaveCommand = new(() => { try { Save(); Hub.Notice = "所有连接参数与界面偏好已保存"; } catch (Exception ex) { Hub.Error(ex); } });
        Transfer(false); SystemSettings.Initialize(); App.ApplyTheme(_preferences.Light);
        try { Simulator.Refresh(); } catch (Exception ex) { Hub.Error(ex); }
    }
    public void Navigate(string key) => SelectedNav = Navigation.FirstOrDefault(x => x.Key == key) ?? Navigation[0];
    private void Transfer(bool save)
    {
        (string, object)[] objects = [("serial", Serial.Config), ("serialSender", Serial.Sender), ("network", Network), ("networkSender", Network.Sender), ("master", Master), ("masterQuery", Master.Query), ("masterSerial", Master.Serial), ("simulator", Simulator), ("simSerial", Simulator.Serial), ("workbench", Workbench), ("builderQuery", Workbench.Query), ("monitor", Monitor)];
        foreach (var (key, obj) in objects) _preferences.Transfer(key, obj, save);
        _preferences.Transfer("plc", Plc, save);
        _preferences.Transfer("mqtt", Mqtt, save);
        _preferences.Transfer("netconfig", NetworkConfig, save);
        _preferences.Transfer("quick", Quick, save);
        _preferences.Transfer("system", SystemSettings, save);
        _preferences.Transfer("virtualSerial", VirtualSerial, save);
    }
    public void Save() { Transfer(true); _preferences.Save(); }
    public async Task Close(bool save = true)
    {
        Quick.Close();
        await VirtualSerial.Close();
        await NetworkConfig.Close();
        await Task.WhenAll(Serial.Close(), Network.Close(), Master.Close(), Simulator.Close(), Plc.Close(), Mqtt.Close());
        if (save) Save();
    }
}
