using System.Drawing;
using System.ComponentModel;
using System.IO;
using System.Windows;
using Forms = System.Windows.Forms;

namespace LinkLab.App;

internal sealed class DesktopLifetime : IDisposable
{
    private readonly ShellViewModel _vm;
    private readonly MainWindow _window;
    private readonly Forms.NotifyIcon _tray;
    private readonly Forms.ContextMenuStrip _menu;
    private readonly Icon _icon;
    private readonly bool _activate;
    private bool _disposed;
    internal DesktopLifetime(ShellViewModel vm, MainWindow window, bool activate = true)
    {
        _vm = vm; _window = window; _activate = activate;
        _icon = Icon.ExtractAssociatedIcon(Path.Combine(AppContext.BaseDirectory, "LinkLab.exe")) ?? (Icon)SystemIcons.Application.Clone();
        _menu = new Forms.ContextMenuStrip();
        _menu.Items.Add("打开 LinkLab", null, (_, _) => ShowMain());
        _menu.Items.Add("系统配置", null, (_, _) => { _vm.Navigate("quick"); ShowMain(); });
        var capture = new Forms.ToolStripMenuItem("启用截图快捷键") { CheckOnClick = false };
        capture.Click += (_, _) => { if (_window.IsEnabled) _vm.Quick.Enabled = !_vm.Quick.Enabled; };
        _menu.Items.Add(capture); _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add("退出 LinkLab", null, (_, _) => Exit());
        _menu.Opening += (_, _) => { capture.Checked = _vm.Quick.Enabled; capture.Enabled = _window.IsEnabled; };
        _tray = new Forms.NotifyIcon { Icon = _icon, Text = "LinkLab · 连接实验室", ContextMenuStrip = _menu, Visible = true };
        _tray.DoubleClick += (_, _) => ShowMain();
        _window.Closed += OnClosed;
        _vm.Quick.PropertyChanged += CaptureChanged;
        _vm.SystemSettings.AttachTray(HideToTray);
    }
    internal void Start(bool background)
    {
        _vm.SystemSettings.ApplyCaptureDefault(_vm.Quick);
        if (!background) ShowMain();
        UpdateTooltip();
    }
    internal void ShowMain()
    {
        if (_disposed || !_window.IsEnabled) return;
        _window.ShowActivated = _activate;
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Show(); if (_activate) _window.Activate();
    }
    internal void HideToTray()
    {
        if (_disposed || !_window.IsEnabled) return;
        _window.Hide(); _vm.Hub.Notice = "已隐藏到托盘 · 双击托盘图标打开界面，右键可退出";
    }
    internal void Exit() { if (!_disposed) _window.Close(); }
    private void CaptureChanged(object? sender, PropertyChangedEventArgs e) { if (e.PropertyName == nameof(QuickVm.Enabled)) UpdateTooltip(); }
    private void UpdateTooltip()
    {
        if (_disposed) return;
        _tray.Text = _vm.Quick.Enabled ? "LinkLab · F1 截图 / F3 贴图" : "LinkLab · 截图已关闭，双击打开主界面";
    }
    private void OnClosed(object? sender, EventArgs e) => Dispose();
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        _window.Closed -= OnClosed; _vm.SystemSettings.AttachTray(null);
        _vm.Quick.PropertyChanged -= CaptureChanged;
        _tray.Visible = false; _tray.Dispose(); _menu.Dispose(); _icon.Dispose();
    }
}
