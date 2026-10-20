using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using LinkLab.Core;

namespace LinkLab.App;

internal static class VirtualSerialSmoke
{
    // A fake backend exercises parameter routing without creating kernel devices or sending serial data.
    private sealed class Backend : IVirtualPortBackend
    {
        public VirtualPortSnapshot Snapshot { get; set; } = new("CNCA0 PortName=COM3\nCNCB0 PortName=COM4", ["COM3", "COM4"], []);
        public TaskCompletionSource<VirtualPortSnapshot>? PendingRead { get; set; }
        public bool FailRead { get; set; }
        public int Changes { get; private set; }
        public Task<VirtualPortSnapshot> Read(CancellationToken ct) => FailRead ? Task.FromException<VirtualPortSnapshot>(new IOException("Device query failed.")) : PendingRead?.Task ?? Task.FromResult(Snapshot);
        public Task<int> Change(IReadOnlyList<string> arguments) { Changes++; throw new IOException("Smoke tests must not mutate Windows devices."); }
    }
    internal static async Task Run(ShellViewModel shell, string output)
    {
        await shell.Master.Close(); await shell.Simulator.Close();
        await CheckDropdownLifecycle(shell);
        var backend = new Backend(); var vm = new VirtualSerialVm(shell, backend);
        Check(!vm.CanCreate && vm.PortAChoices.Length == 0 && vm.PortBChoices.Length == 0, "Unqueried ports were selectable.");
        await vm.Refresh();
        Check(!vm.PortAChoices.Contains("COM3") && !vm.PortBChoices.Contains("COM4"), "Existing pair names were offered for creation.");
        if (vm.Pairs.Count != 1 || vm.Selected?.Present != true) throw new IOException("Virtual serial pair selection failed.");
        vm.Baud = "19200"; vm.Unit = "7"; await vm.ConfigureRtu();
        if (!shell.Master.IsRtu || !shell.Simulator.IsRtu || shell.Master.Serial.Port != "COM3" || shell.Simulator.Serial.Port != "COM4" || shell.Master.Serial.Baud != "19200" || shell.Simulator.Serial.Baud != "19200" || shell.Master.Query.Unit != "7" || shell.Simulator.Unit != "7" || shell.Master.Query.FunctionIndex != 2 || shell.Master.Query.Quantity != "3")
            throw new IOException("Virtual serial RTU parameter routing failed.");
        backend.Snapshot = new("", [], []);
        bool rejected = false; try { await vm.ConfigureRtu(); } catch (InvalidOperationException) { rejected = true; }
        if (!rejected) throw new IOException("Virtual serial stale pair was accepted.");
        await vm.Refresh(); if (vm.Selected is not null || vm.Pairs.Count != 0) throw new IOException("Stale pair selection survived refresh.");
        vm.ToolPath = ""; if (vm.CanManage || vm.CanUsePair) throw new IOException("Missing driver tool enabled device operations.");
        vm.ToolPath = Path.Combine(AppContext.BaseDirectory, "LinkLab.exe");
        vm.PortA = "COM3"; vm.PortB = "COM4";
        // Occupancy changes after the dropdown was loaded: creation must still recheck.
        backend.Snapshot = new("", ["COM3", "COM4"], ["COM3", "COM4", "COM5"]);
        vm.CreateCommand.Execute(null);
        while (!vm.CreateCommand.CanExecute(null)) await Task.Delay(10);
        if (!vm.HasError || !vm.Status.Contains("COM3、COM4") || vm.Busy || vm.CreateLabel != "创建端口对")
            throw new IOException("Create conflict did not restore controls or expose the conflicting names.");
        vm.SuggestCommand.Execute(null);
        while (!vm.SuggestCommand.CanExecute(null)) await Task.Delay(10);
        if (vm.HasError || vm.PortA != "COM6" || vm.PortB != "COM7") throw new IOException("Free-port suggestion did not recover from conflict.");
        backend.Snapshot = new("CNCA0 PortName=COM6\nCNCB0 PortName=COM7", ["com3", "COM4"], ["COM5"]);
        await vm.Refresh();
        Check(vm.PortA == "COM8" && vm.PortB == "COM9" && vm.CanCreate, "Unavailable saved selections were not replaced by free names.");
        Check(Enumerable.Range(3, 5).All(n => !vm.PortAChoices.Contains($"COM{n}") && !vm.PortBChoices.Contains($"COM{n}")), "Bluetooth, reserved or unready pair names remained selectable.");
        Check(vm.PortAChoices.Contains("COM1") && vm.PortBChoices.Contains("COM4096"), "Valid port-number boundaries were omitted.");
        vm.PortA = "COM12"; vm.PortB = "COM15";
        Check(!vm.PortAChoices.Contains("COM15") && !vm.PortBChoices.Contains("COM12") && vm.PortBChoices.Contains("COM8"), "Endpoint choices did not exclude the opposite selection or release the previous one.");
        await vm.Refresh(); Check(vm.PortA == "COM12" && vm.PortB == "COM15", "Refresh discarded valid selections.");
        backend.PendingRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.RefreshCommand.Execute(null);
        Check(vm.Busy && !vm.CanCreate && vm.PortAChoices.Length == 0 && vm.PortBChoices.Length == 0, "Stale choices survived an in-flight query.");
        backend.PendingRead.SetResult(backend.Snapshot);
        while (!vm.RefreshCommand.CanExecute(null)) await Task.Delay(10);
        backend.PendingRead = null;
        var allPorts = Enumerable.Range(1, 4096).Select(n => $"COM{n}").ToArray();
        backend.Snapshot = new("", [], allPorts.Where(port => port != "COM20").ToArray()); await vm.Refresh();
        Check(!vm.CanCreate && vm.PortA == "COM20" && vm.PortB == "" && vm.PortBChoices.Length == 0, "Only one free name allowed creation.");
        backend.Snapshot = new("", [], allPorts); await vm.Refresh();
        Check(!vm.CanCreate && vm.PortAChoices.Length == 0 && vm.PortBChoices.Length == 0 && vm.PortChoiceHint.Contains("不足两个"), "Exhausted names were not handled.");
        backend.Snapshot = new("", [], allPorts.Where(port => port is not "COM10" and not "COM12").ToArray());
        vm.SuggestCommand.Execute(null); while (!vm.SuggestCommand.CanExecute(null)) await Task.Delay(10);
        Check(vm.CanCreate && vm.PortA == "COM10" && vm.PortB == "COM12", "Nonadjacent free names could not be selected.");
        backend.FailRead = true;
        vm.RefreshCommand.Execute(null); while (!vm.RefreshCommand.CanExecute(null)) await Task.Delay(10);
        Check(vm.HasError && !vm.CanCreate && vm.PortAChoices.Length == 0 && vm.PortBChoices.Length == 0, "Failed refresh left stale choices selectable.");
        Check(backend.Changes == 0, "Rejected creation invoked the driver.");
        await vm.Close();
        // Exercise the real, read-only Windows backend with the installed admin-only setupc.
        // Read must not inspect or launch this tool, so it also works with a missing tool path.
        var real = await new VirtualSerialBackend(() => throw new IOException("Query attempted to launch setupc")).Read(CancellationToken.None);
        _ = real.Pairs;
        if (VirtualPortDevices.ResolveName("CNCA4", "COM#", "COM15") != "COM15" ||
            VirtualPortDevices.ResolveName("CNCB4", "CNCB4", null) != "CNCB4" ||
            VirtualPortDevices.ResolveName("CNCA4", "COM#", null) != "COM#") throw new IOException("Registry name mapping failed.");
        rejected = false;
        try { VirtualPortDevices.ResolveName("CNCA4", "COM3\nCNCB4 PortName=COM4", null); } catch (IOException) { rejected = true; }
        if (!rejected) throw new IOException("Registry listing injection was accepted.");
        // Restore screenshot demo settings; the fake COM names were never opened.
        shell.Master.Mode = 0; shell.Simulator.Mode = 0; shell.Master.Query.Unit = "1"; shell.Simulator.Unit = "1"; shell.Master.Query.Quantity = "10";
        shell.Hub.Notice = "准备就绪 · 所有通信在本机执行";
        File.WriteAllText(Path.Combine(output, "virtual-serial-result.txt"), $"PASS: free-port dropdowns exclude Bluetooth, reserved and unready pair names; mutual exclusion, preserved selection, occupied selection recovery, full COM1..4096 range, zero/one/nonadjacent free names, pending/failed query invalidation and last-moment occupancy refusal without driver calls. Pair selection, RTU parameter routing, stale pair refusal, missing tool, error/busy recovery, read-only Windows query, COM# mapping and malformed data refusal passed.\nDetected {real.Pairs.Count} real pairs and {real.PresentPorts.Length} COM ports. Device issues: {string.Join("; ", real.DeviceIssues.Values.Select(issue => issue.Message))}\nNo Windows devices were changed by this smoke test.\n");
    }
    private static async Task CheckDropdownLifecycle(ShellViewModel shell)
    {
        var backend = new Backend { Snapshot = new("", ["COM3", "COM4"], ["COM5"]) };
        var vm = new VirtualSerialVm(shell, backend) { ToolPath = Path.Combine(AppContext.BaseDirectory, "LinkLab.exe") };
        var page = new ContentControl { Content = vm, Width = 1100, Height = 850 };
        async Task Layout()
        {
            page.Measure(new Size(1100, 850)); page.Arrange(new Rect(0, 0, 1100, 850)); page.UpdateLayout();
            await page.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            page.UpdateLayout();
        }
        void CheckSelections(string stage)
        {
            var a = App.Descendants<ComboBox>(page).Single(combo => combo.Name == "CreatePortACombo");
            var b = App.Descendants<ComboBox>(page).Single(combo => combo.Name == "CreatePortBCombo");
            Check(a.SelectedItem is string && b.SelectedItem is string && (string)a.SelectedItem == vm.PortA && (string)b.SelectedItem == vm.PortB, $"Dropdown selections blank after {stage}.");
            Check(Equals(((ContentPresenter)a.Template.FindName("Display", a)).Content, vm.PortA) && Equals(((ContentPresenter)b.Template.FindName("Display", b)).Content, vm.PortB), $"Dropdown display blank after {stage}.");
        }
        try
        {
            await Layout();
            var pending = new TaskCompletionSource<VirtualPortSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            backend.PendingRead = pending;
            vm.RefreshCommand.Execute(null); await Layout();
            Check(vm.Busy && !vm.CanCreate, "Pending device refresh allowed creation.");
            await Task.Run(() => pending.SetResult(backend.Snapshot));
            while (!vm.RefreshCommand.CanExecute(null)) await Task.Delay(10);
            backend.PendingRead = null; await Layout(); CheckSelections("asynchronous initial detection");
            var a = App.Descendants<ComboBox>(page).Single(combo => combo.Name == "CreatePortACombo");
            a.SetCurrentValue(ComboBox.SelectedItemProperty, "COM20"); await Layout(); CheckSelections("user endpoint selection");
            backend.Snapshot = new("", [vm.PortA, vm.PortB], []);
            vm.CreateCommand.Execute(null); while (!vm.CreateCommand.CanExecute(null)) await Task.Delay(10);
            await Layout(); CheckSelections("creation conflict and automatic refresh");
            Check(vm.HasError && backend.Changes == 0, "Conflict test unexpectedly changed a Windows device.");
            backend.Snapshot = new("CNCA0 PortName=COM6\nCNCB0 PortName=COM7", ["COM3", "COM4"], ["COM5"])
            {
                DeviceIssues = new Dictionary<int, VirtualPortIssue> { [0] = new(52, VirtualPortDevices.ProblemMessage(52)) }
            };
            pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            backend.PendingRead = pending;
            vm.RefreshCommand.Execute(null); await Layout();
            await Task.Run(() => pending.SetResult(backend.Snapshot));
            while (!vm.RefreshCommand.CanExecute(null)) await Task.Delay(10);
            backend.PendingRead = null; await Layout(); CheckSelections("asynchronous blocked-driver detection");
            Check(vm.DriverBlocked && !vm.CanCreate && vm.CanDeletePair && vm.DriverRecoveryHint.Contains("管理员运行也无法解决"), "Blocked driver did not disable creation while retaining targeted cleanup.");
            Check(!App.Descendants<Button>(page).Single(button => button.Name == "CreatePortPairButton").IsEnabled, "Blocked-driver creation button remained enabled.");
            vm.CreateCommand.Execute(null); while (!vm.CreateCommand.CanExecute(null)) await Task.Delay(10);
            await Layout(); CheckSelections("blocked creation refusal");
            Check(backend.Changes == 0, "Blocked driver invoked a device mutation.");
        }
        finally { page.Content = null; await vm.Close(); }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new IOException(message); }
}
