using LinkLab.Core;

internal static class VirtualSerialTests
{
    private const string Listing = "  CNCA0 PortName=COM3,EmuBR=yes\r\n  CNCB0 PortName=COM4,EmuBR=yes\r\n";
    private static void Check(bool condition) { if (!condition) throw new Exception("Virtual serial assertion failed"); }
    private static void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new Exception($"Expected {typeof(T).Name}"); }
    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception { try { await action(); } catch (T) { return; } throw new Exception($"Expected {typeof(T).Name}"); }
    private static Task Sync(Action action) { action(); return Task.CompletedTask; }
    internal static async Task Run(Func<string, Func<Task>, Task> test)
    {
        await test("Virtual serial COM names reject malformed and command-injection input", () => Sync(() =>
        {
            Check(VirtualSerial.PortName(" com3 ") == "COM3"); Check(VirtualSerial.PortName("COM4096") == "COM4096");
            foreach (string invalid in new[] { "COM0", "COM01", "COM4097", "COM3 --help", "COM3,PortName=COM4", "COM3\nremove 0", "CNCA0", "COM#", "COM٣", "" }) Throws<ArgumentException>(() => VirtualSerial.PortName(invalid));
            Throws<ArgumentException>(() => VirtualSerial.CreateArguments("com3", "COM3", []));
        }));
        await test("Virtual serial parser pairs unordered IDs and standard COM class aliases", () => Sync(() =>
        {
            var pairs = VirtualSerial.ParsePairs("Driver header\n CNCB9 PortName=COM#,RealPortName=COM11\n" + Listing + " CNCA9 EmuBR=yes,PortName=COM#,RealPortName=COM10\n");
            Check(pairs.Count == 2 && pairs[0] == new VirtualPortPair(0, "COM3", "COM4") && pairs[1] == new VirtualPortPair(9, "COM10", "COM11"));
            Check(VirtualSerial.ParsePairs("CNCA1 -\nCNCB1 -")[0] == new VirtualPortPair(1, "CNCA1", "CNCB1"));
            Check(!VirtualSerial.ParsePairs("CNCA2 PortName=COM7")[0].HasComNames);
            Throws<FormatException>(() => VirtualSerial.ParsePairs(Listing + Listing));
            Throws<FormatException>(() => VirtualSerial.ParsePairs("CNCA0 PortName=COM3,PortName=COM7"));
        }));
        await test("Virtual serial creation blocks active reserved and existing pair names", () => Sync(() =>
        {
            var snapshot = new VirtualPortSnapshot(Listing, ["com7"], ["COM9"]);
            foreach (string used in new[] { "COM3", "com4", "COM7", "com9" }) Throws<InvalidOperationException>(() => VirtualSerial.CreateArguments(used, "COM20", snapshot.UnavailableNames));
            Check(VirtualSerial.Suggest(snapshot.UnavailableNames) == ("COM5", "COM6"));
            var arguments = VirtualSerial.CreateArguments("com5", "com6", snapshot.UnavailableNames);
            Check(arguments.Contains("install") && arguments.Contains("PortName=COM5,EmuBR=yes,EmuOverrun=yes") && !arguments.Contains("uninstall") && !arguments.Contains("--no-update"));
            try { VirtualSerial.CreateArguments("COM3", "COM4", ["COM3", "COM4"]); }
            catch (InvalidOperationException ex) { Check(ex.Message.Contains("COM3、COM4") && ex.Message.Contains("选择空闲端口")); }
        }));
        await test("Virtual serial refuses stale deletion and missing or ambiguous test pairs", () => Sync(() =>
        {
            var pair = new VirtualPortPair(0, "COM3", "COM4");
            var snapshot = new VirtualPortSnapshot(Listing, ["COM3", "com4"], []);
            Check(VirtualSerial.RequirePresent(pair, snapshot) == pair);
            Check(VirtualSerial.RemoveArguments(pair, snapshot).TakeLast(2).SequenceEqual(new[] { "remove", "0" }));
            Throws<InvalidOperationException>(() => VirtualSerial.RemoveArguments(new(0, "COM5", "COM6"), snapshot));
            var partial = new VirtualPortSnapshot("CNCA0 PortName=COM3", ["COM3"], []);
            Throws<InvalidOperationException>(() => VirtualSerial.RemoveArguments(partial.Pairs[0], partial));
            Throws<InvalidOperationException>(() => VirtualSerial.RequirePresent(pair, snapshot with { PresentPorts = ["COM3"] }));
            Throws<InvalidOperationException>(() => VirtualSerial.RequirePresent(pair, snapshot with { Listing = Listing + "CNCA1 PortName=COM3\nCNCB1 PortName=COM9" }));
        }));
        await test("Virtual serial manager verifies creation and removes only selected pair", async () =>
        {
            var backend = new FakeBackend(); var manager = new VirtualPortManager(backend);
            backend.AfterChange = new(Listing, ["COM3", "COM4"], ["COM3", "COM4"]);
            var pair = await manager.Create("COM3", "COM4"); Check(pair == new VirtualPortPair(0, "COM3", "COM4") && backend.Changes == 1);
            backend.AfterChange = new("", [], []); await manager.Remove(pair); Check(backend.Changes == 2 && backend.Arguments[^2] == "remove" && backend.Arguments[^1] == "0");
        });
        await test("Virtual serial manager rejects nominal success with missing driver endpoints", async () =>
        {
            var backend = new FakeBackend { AfterChange = new(Listing, ["COM3"], []) }; var manager = new VirtualPortManager(backend);
            await ThrowsAsync<IOException>(() => manager.Create("COM3", "COM4")); Check(backend.Changes == 1);
            backend.Snapshot = new(Listing, ["COM3", "COM4"], []); backend.AfterChange = backend.Snapshot;
            await ThrowsAsync<IOException>(() => manager.Remove(new(0, "COM3", "COM4"))); Check(backend.Changes == 2);
        });
        await test("Virtual serial manager rechecks stale names before elevation", async () =>
        {
            var backend = new FakeBackend { Snapshot = new(Listing, ["COM3", "COM4"], ["COM5"]) }; var manager = new VirtualPortManager(backend);
            await ThrowsAsync<InvalidOperationException>(() => manager.Create("COM5", "COM6"));
            await ThrowsAsync<InvalidOperationException>(() => manager.Remove(new(0, "COM7", "COM8"))); Check(backend.Changes == 0);
        });
        await test("Virtual serial cancelled authorization leaves existing devices intact", async () =>
        {
            var backend = new FakeBackend { Snapshot = new(Listing, ["COM3", "COM4"], []), CancelChange = true }; var manager = new VirtualPortManager(backend);
            await ThrowsAsync<OperationCanceledException>(() => manager.Remove(new(0, "COM3", "COM4"))); Check(backend.Snapshot.Listing == Listing && backend.Changes == 1);
            using var stop = new CancellationTokenSource(); stop.Cancel();
            await ThrowsAsync<OperationCanceledException>(() => manager.Create("COM5", "COM6", stop.Token)); Check(backend.Changes == 1);
        });
        await test("Virtual serial reports blocked driver even when setupc returns success", async () =>
        {
            var issue = new VirtualPortIssue(52, "Windows 无法验证驱动数字签名（代码 52）。");
            var blocked = new VirtualPortSnapshot(Listing, ["COM3", "COM4"], []) { DeviceIssues = new Dictionary<int, VirtualPortIssue> { [0] = issue } };
            Check(!blocked.IsPresent(blocked.Pairs[0]));
            var backend = new FakeBackend { AfterChange = blocked }; var manager = new VirtualPortManager(backend);
            try { await manager.Create("COM3", "COM4"); throw new Exception("Blocked driver reported success"); }
            catch (IOException ex) { Check(ex.Message.Contains("52") && backend.Changes == 1); }
            await ThrowsAsync<IOException>(() => manager.Create("COM6", "COM7")); Check(backend.Changes == 1);
            backend.Snapshot = blocked with { DeviceIssues = new Dictionary<int, VirtualPortIssue> { [0] = new(48, "Windows 已阻止驱动（代码 48）。") } };
            await ThrowsAsync<IOException>(() => manager.Create("COM6", "COM7")); Check(backend.Changes == 1);
        });
    }

    private sealed class FakeBackend : IVirtualPortBackend
    {
        public VirtualPortSnapshot Snapshot { get; set; } = new("", [], []);
        public VirtualPortSnapshot AfterChange { get; set; } = new("", [], []);
        public bool CancelChange { get; init; }
        public int Changes { get; private set; }
        public string[] Arguments { get; private set; } = [];
        public Task<VirtualPortSnapshot> Read(CancellationToken ct) { ct.ThrowIfCancellationRequested(); return Task.FromResult(Snapshot); }
        public Task<int> Change(IReadOnlyList<string> arguments)
        {
            Changes++; Arguments = arguments.ToArray();
            if (CancelChange) throw new OperationCanceledException();
            Snapshot = AfterChange; return Task.FromResult(0);
        }
    }
}
