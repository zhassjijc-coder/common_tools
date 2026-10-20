namespace LinkLab.App;

public sealed class SystemSettingsVm : PageVm
{
    private readonly IStartupRegistration _startup;
    private readonly Action _save;
    private bool _captureOnStartup, _autoStart, _ready, _differentVersion;
    private string _startupStatus = "开机自启动未开启。";
    private Action? _hideToTray;
    [Saved] public bool CaptureOnStartup
    {
        get => _captureOnStartup;
        set
        {
            bool previous = _captureOnStartup;
            if (!Set(ref _captureOnStartup, value) || !_ready) return;
            try { _save(); Hub.Notice = value ? "已保存：下次启动自动启用 F1 / F3 截图快捷键" : "已保存：下次启动不自动启用截图快捷键"; }
            catch (Exception ex) { _captureOnStartup = previous; Raise(); Hub.Error(ex); }
        }
    }
    // Windows is the source of truth; do not recreate a disabled startup entry when loading preferences.
    public bool AutoStart
    {
        get => _autoStart;
        set
        {
            if (value == _autoStart) return;
            SetAutoStart(value);
        }
    }
    public string StartupStatus { get => _startupStatus; private set => Set(ref _startupStatus, value); }
    public bool DifferentVersion { get => _differentVersion; private set => Set(ref _differentVersion, value); }
    public bool CanHideToTray => _hideToTray is not null;
    public Command RefreshCommand { get; }
    public Command UpdateStartupCommand { get; }
    public Command HideToTrayCommand { get; }
    public SystemSettingsVm(Hub hub, Action save, IStartupRegistration startup) : base(hub)
    {
        _save = save; _startup = startup;
        RefreshCommand = new(Refresh);
        UpdateStartupCommand = new(() => SetAutoStart(true));
        HideToTrayCommand = new(() => _hideToTray?.Invoke());
    }
    public void Initialize() { _ready = true; Refresh(); }
    public void Refresh()
    {
        try
        {
            string? command = _startup.Read();
            _autoStart = !string.IsNullOrWhiteSpace(command); Raise(nameof(AutoStart));
            DifferentVersion = _autoStart && !string.Equals(command, StartupCommand.Build(_startup.ExecutablePath), StringComparison.OrdinalIgnoreCase);
            StartupStatus = !_autoStart ? "未开启 · 登录 Windows 后不会自动运行 LinkLab。"
                : DifferentVersion ? "启动项指向其他版本或参数，点击「更新为当前版本」后使用当前程序。"
                : "已登记当前版本 · 登录 Windows 后静默驻留托盘，不弹出主窗口。";
        }
        catch (Exception ex) { StartupStatus = "读取启动项失败：" + ex.Message; Hub.Error(ex); }
    }
    private void SetAutoStart(bool enabled)
    {
        try
        {
            string? desired = enabled ? StartupCommand.Build(_startup.ExecutablePath) : null;
            _startup.Write(desired);
            if (!string.Equals(_startup.Read(), desired, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("启动项没有成功保存，请刷新检查 Windows 权限或安全软件设置。");
            Refresh(); Hub.Notice = enabled ? "开机自启动已开启 · 下次登录 Windows 时在后台运行" : "开机自启动已关闭";
        }
        catch (Exception ex) { Refresh(); StartupStatus = "自启动设置失败：" + ex.Message; Hub.Error(ex); Raise(nameof(AutoStart)); }
    }
    internal void AttachTray(Action? hide) { _hideToTray = hide; Raise(nameof(CanHideToTray)); }
    internal void ApplyCaptureDefault(QuickVm capture) { if (CaptureOnStartup) capture.Enabled = true; }
}
