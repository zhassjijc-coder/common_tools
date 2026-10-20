using System.IO;
using System.Security.Principal;
using LinkLab.Core;

namespace LinkLab.App;

// Explicit integration check only; never included in the ordinary --smoke-test run.
// --driver-check --create-pair COM3 COM4 <output> creates the requested pair and leaves it available.
internal static class VirtualSerialDriverCheck
{
    internal static async Task Run(string[] args, string output)
    {
        var shell = new ShellViewModel(false, SystemSettingsSmoke.CreateRegistration());
        var vm = shell.VirtualSerial;
        string report = Path.Combine(output, "driver-check.txt");
        void Log(string message) => File.AppendAllText(report, $"[{DateTime.Now:HH:mm:ss}] {message}\n");
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.Status)) Log(vm.Status); };
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            Log($"Administrator token: {new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)}; read-only detection never elevates or changes devices.");
            await vm.Refresh();
            int create = Array.IndexOf(args, "--create-pair");
            if (create < 0) { Log("READ ONLY: no devices changed."); return; }
            if (create + 2 >= args.Length - 1) throw new ArgumentException("--create-pair requires two COM names before the output directory.");
            vm.PortA = VirtualSerial.PortName(args[create + 1]); vm.PortB = VirtualSerial.PortName(args[create + 2]);
            if (vm.Pairs.Any(p => p.PortA == vm.PortA || p.PortB == vm.PortB || p.PortB == vm.PortA || p.PortA == vm.PortB))
                throw new IOException("指定端口已属于现有端口对，本次检查未修改或打开这些端口。");
            Log($"CREATE requested: {vm.PortA} / {vm.PortB}; tool: {vm.ToolPath}");
            await Execute(vm.CreateCommand, vm);
            Log("CREATE PASS");
            await Execute(vm.DuplexCommand, vm); Log("DUPLEX PASS");
            await Execute(vm.RtuCommand, vm); Log("RTU FC03 / FC06 / readback PASS");
            Log($"Pair retained for use: {vm.Selected?.Pair.Label}; all test handles closed.");
        }
        catch (Exception ex) { Log("FAILED: " + ex); throw; }
        finally { await shell.Close(false); }
    }
    private static async Task Execute(AsyncCommand command, VirtualSerialVm vm)
    {
        command.Execute(null);
        while (!command.CanExecute(null)) await Task.Delay(50);
        if (vm.HasError) throw new IOException(vm.Status);
    }
}
