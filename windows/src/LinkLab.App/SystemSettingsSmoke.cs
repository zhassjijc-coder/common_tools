using System.IO;
using System.Text.Json;
using System.Windows;

namespace LinkLab.App;

internal static class SystemSettingsSmoke
{
    internal static IStartupRegistration CreateRegistration() => new Registration();
    private sealed class Registration : IStartupRegistration
    {
        public string ExecutablePath => @"C:\Program Files\LinkLab 测试\LinkLab.exe";
        public string? Value { get; set; }
        public int Writes { get; private set; }
        public bool RejectWrite { get; set; }
        public string? Read() => Value;
        public void Write(string? command) { if (RejectWrite) throw new UnauthorizedAccessException("Expected startup permission failure"); Writes++; Value = command; }
    }
    private sealed class Hotkeys : ICaptureHotkeys
    {
        public int Registrations { get; private set; }
        public bool Conflict { get; init; }
        public void Enable(Action capture, Action pin) { if (Conflict) throw new InvalidOperationException("F1 已被其他程序占用。"); Registrations++; }
        public void Dispose() { }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new IOException("System settings smoke: " + message); }
    internal static async Task Run(string output)
    {
        var report = new List<string>();
        string? realStartupBefore = new WindowsStartupRegistration().Read();
        var store = new Registration(); int saves = 0; var hub = new Hub();
        var settings = new SystemSettingsVm(hub, () => saves++, store);
        settings.Initialize();
        Check(!settings.AutoStart && !settings.CaptureOnStartup && store.Writes == 0 && saves == 0, "fresh defaults must be off without writes");
        settings.AutoStart = true;
        Check(store.Value == "\"C:\\Program Files\\LinkLab 测试\\LinkLab.exe\" --background" && settings.AutoStart && !settings.CaptureOnStartup, "startup must quote the EXE and be independent from capture");
        settings.AutoStart = false; Check(store.Value is null && !settings.AutoStart, "disable must remove the registration");
        store.RejectWrite = true; settings.AutoStart = true;
        Check(!settings.AutoStart && settings.StartupStatus.Contains("失败"), "failed startup write must not show enabled");
        store.RejectWrite = false; store.Value = "\"C:\\Old LinkLab\\LinkLab.exe\" --background"; int writes = store.Writes;
        settings.Refresh(); Check(settings.AutoStart && settings.DifferentVersion && store.Writes == writes, "refresh must not silently rebind another version");
        settings.UpdateStartupCommand.Execute(null); Check(!settings.DifferentVersion && store.Value == StartupCommand.Build(store.ExecutablePath), "explicit rebind must use current version");
        foreach (string invalid in new[] { "LinkLab.exe", "C:\\test\".exe", "C:\\" + new string('x', 260) + ".exe" })
        {
            bool rejected = false; try { StartupCommand.Build(invalid); } catch (ArgumentException) { rejected = true; }
            Check(rejected, "unsafe or oversized startup command accepted");
        }
        report.Add("PASS startup preferences: opt-in defaults; quoted background command; enable/disable; failed-write rollback; explicit version rebind; command validation (fake registration backend).");

        var prefs = new Preferences(); settings.CaptureOnStartup = true; prefs.Transfer("system", settings, true);
        var restoredPrefs = JsonSerializer.Deserialize<Preferences>(JsonSerializer.Serialize(prefs))!;
        int loadSaves = 0; var restored = new SystemSettingsVm(hub, () => loadSaves++, new Registration());
        restoredPrefs.Transfer("system", restored, false); restored.Initialize();
        Check(restored.CaptureOnStartup && loadSaves == 0, "loading persisted capture default must not rewrite settings");
        var hotkeys = new Hotkeys(); var capture = new QuickVm(hub, hotkeys);
        restored.ApplyCaptureDefault(capture); Check(capture.Enabled && hotkeys.Registrations == 1, "saved default must enable hotkeys at startup");
        capture.Enabled = false; Check(restored.CaptureOnStartup, "current capture toggle must not change next-start preference"); capture.Close();
        var conflict = new QuickVm(hub, new Hotkeys { Conflict = true }); restored.ApplyCaptureDefault(conflict);
        Check(!conflict.Enabled && restored.CaptureOnStartup && conflict.Status.Contains("F1"), "hotkey conflict must leave capture off and preserve preference"); conflict.Close();
        var saveFailure = new SystemSettingsVm(hub, () => throw new IOException("Expected save failure"), new Registration()); saveFailure.Initialize(); saveFailure.CaptureOnStartup = true;
        Check(!saveFailure.CaptureOnStartup, "failed preference save must restore checkbox state");
        report.Add("PASS capture default: preference serialization/reload; startup registration; independence from current toggle; conflict reporting; save-failure rollback (fake hotkeys, no F1/F3 occupied).");

        string instanceName = "Local\\LinkLab-Verification-" + Guid.NewGuid().ToString("N");
        using (var primary = new SingleInstance(instanceName))
        using (var secondary = new SingleInstance(instanceName))
        {
            Check(primary.IsPrimary && !secondary.IsPrimary, "resident instance detection failed");
            var shown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            secondary.RequestShow(); // Wake-up must survive arriving before the listener is ready.
            primary.Listen(() => shown.TrySetResult());
            await shown.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }
        using (var next = new SingleInstance(instanceName)) Check(next.IsPrimary, "instance ownership not released on exit");
        report.Add("PASS single instance: second launch requests activation, startup race preserves activation event, exit releases named objects (isolated verification name).");

        foreach (bool showThenHide in new[] { false, true })
        {
            var shell = new ShellViewModel(false, new Registration());
            var window = new MainWindow(shell, false) { WindowStartupLocation = WindowStartupLocation.Manual, Left = -16000, Top = 0, ShowActivated = false, ShowInTaskbar = false };
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); window.Closed += (_, _) => closed.TrySetResult();
            using var desktop = new DesktopLifetime(shell, window, activate: false);
            try
            {
                desktop.Start(background: true);
                Check(!window.IsVisible && !shell.Quick.Enabled && shell.SystemSettings.CanHideToTray, "background launch must keep the main window hidden");
                if (showThenHide)
                {
                    desktop.ShowMain(); Check(window.IsVisible, "tray restore did not show main window");
                    desktop.HideToTray(); Check(!window.IsVisible, "hide-to-tray did not hide main window");
                    desktop.ShowMain(); Check(window.IsVisible, "second restore failed");
                }
                desktop.Exit(); await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Check(!window.IsVisible && !shell.SystemSettings.CanHideToTray, "tray or window survived exit");
            }
            finally { await shell.Close(false); }
        }
        Check(realStartupBefore == new WindowsStartupRegistration().Read(), "verification modified the real startup entry");
        report.Add("PASS tray lifecycle: silent background launch, restore, hide, restore again, exit from a never-shown window, tray cleanup. Temporary tray icons removed; real startup entry unchanged.");
        File.WriteAllLines(Path.Combine(output, "system-settings-result.txt"), report);
    }
}
