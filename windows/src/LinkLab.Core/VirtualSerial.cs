using System.Globalization;
using System.Text.RegularExpressions;

namespace LinkLab.Core;

public sealed record VirtualPortPair(int Id, string PortA, string PortB)
{
    public string Label => $"{PortA} ↔ {PortB}";
    public bool HasComNames => VirtualSerial.IsComName(PortA) && VirtualSerial.IsComName(PortB) && !PortA.Equals(PortB, StringComparison.OrdinalIgnoreCase);
}

public sealed record VirtualPortIssue(uint Code, string Message);

public sealed record VirtualPortSnapshot(string Listing, string[] PresentPorts, string[] ReservedPorts)
{
    public IReadOnlyDictionary<int, VirtualPortIssue> DeviceIssues { get; init; } = new Dictionary<int, VirtualPortIssue>();
    public IReadOnlyList<VirtualPortPair> Pairs => VirtualSerial.ParsePairs(Listing);
    public IEnumerable<string> UnavailableNames => PresentPorts.Concat(ReservedPorts).Concat(Pairs.SelectMany(p => new[] { p.PortA, p.PortB }));
    public bool IsPresent(VirtualPortPair pair) => !DeviceIssues.ContainsKey(pair.Id) && pair.HasComNames && new[] { pair.PortA, pair.PortB }.All(p => PresentPorts.Contains(p, StringComparer.OrdinalIgnoreCase));
}

public interface IVirtualPortBackend
{
    Task<VirtualPortSnapshot> Read(CancellationToken ct);
    // A mutation must wait for the driver tool to exit; cancellation must not abandon a device operation.
    Task<int> Change(IReadOnlyList<string> arguments);
}

public static partial class VirtualSerial
{
    [GeneratedRegex(@"^COM([1-9][0-9]{0,3})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ComPattern();
    [GeneratedRegex(@"^\s*CNC([AB])([0-9]{1,6})\s+(.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PairPattern();

    public static bool IsComName(string value) => ComPattern().Match(value) is { Success: true } match && int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) <= 4096;
    public static string PortName(string value)
    {
        string name = value.Trim().ToUpperInvariant();
        if (!IsComName(name)) throw new ArgumentException("端口名称应为 COM1–COM4096，例如 COM3。");
        return name;
    }

    public static IReadOnlyList<VirtualPortPair> ParsePairs(string output)
    {
        var sides = new Dictionary<(int Id, char Side), string>();
        foreach (string line in output.Split('\n'))
        {
            var match = PairPattern().Match(line.TrimEnd('\r'));
            if (!match.Success) continue;
            int id = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
            char side = char.ToUpperInvariant(match.Groups[1].Value[0]);
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string parameter in match.Groups[3].Value.Split(','))
            {
                string[] kv = parameter.Trim().Split('=', 2);
                if (kv.Length == 2 && !values.TryAdd(kv[0].Trim(), kv[1].Trim())) throw new FormatException("驱动返回重复参数，请检查 setupc 版本。");
            }
            // Default CNCA/CNCB ports may have an empty parameter list shown as '-'.
            string name = values.GetValueOrDefault("PortName", $"CNC{side}{id}");
            if (name.Equals("COM#", StringComparison.OrdinalIgnoreCase)) name = values.GetValueOrDefault("RealPortName", "COM#");
            if (!sides.TryAdd((id, side), name.ToUpperInvariant())) throw new FormatException("驱动返回重复端口编号，已停止操作。");
        }
        return sides.Keys.Select(k => k.Id).Distinct().Order().Select(id => new VirtualPortPair(id,
            sides.GetValueOrDefault((id, 'A'), "未枚举"), sides.GetValueOrDefault((id, 'B'), "未枚举"))).ToArray();
    }

    public static string[] CreateArguments(string portA, string portB, IEnumerable<string> unavailable)
    {
        string a = PortName(portA), b = PortName(portB);
        if (a == b) throw new ArgumentException("一对虚拟串口必须使用两个不同的 COM 名称。");
        var used = unavailable.ToHashSet(StringComparer.OrdinalIgnoreCase);
        string[] conflicts = new[] { a, b }.Where(used.Contains).ToArray();
        if (conflicts.Length > 0)
            throw new InvalidOperationException($"{string.Join("、", conflicts)} 已存在或被 Windows 保留，请点击「选择空闲端口」后再创建。");
        // Binding the installed driver is essential for new devices. --no-update would only
        // register an empty bus and can return success without producing usable COM ports.
        return ["--silent", "--wait", "30", "install", $"PortName={a},EmuBR=yes,EmuOverrun=yes", $"PortName={b},EmuBR=yes,EmuOverrun=yes"];
    }

    public static string[] RemoveArguments(VirtualPortPair expected, VirtualPortSnapshot current)
    {
        if (expected.Id is < 0 or > 999999 || expected.PortA == "未枚举" || expected.PortB == "未枚举" || !current.Pairs.Contains(expected)) throw new InvalidOperationException("端口对已变化或枚举不完整，请刷新后重新选择；没有删除任何端口。");
        return ["--silent", "--wait", "30", "remove", expected.Id.ToString(CultureInfo.InvariantCulture)];
    }

    public static (string A, string B) Suggest(IEnumerable<string> unavailable)
    {
        var used = unavailable.ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (int n = 3; n < 4096; n++)
            if (!used.Contains($"COM{n}") && !used.Contains($"COM{n + 1}")) return ($"COM{n}", $"COM{n + 1}");
        throw new InvalidOperationException("未找到两个连续空闲的 COM 名称。");
    }

    public static VirtualPortPair RequirePresent(VirtualPortPair expected, VirtualPortSnapshot current)
    {
        if (!current.Pairs.Contains(expected) || !current.IsPresent(expected)) throw new InvalidOperationException("端口对不再存在或驱动尚未就绪，请刷新并检查设备管理器。");
        if (current.Pairs.SelectMany(p => new[] { p.PortA, p.PortB }).Count(p => p == expected.PortA || p == expected.PortB) != 2)
            throw new InvalidOperationException("检测到重复 COM 名称，无法安全测试该端口对。");
        return expected;
    }
}

public sealed class VirtualPortManager(IVirtualPortBackend backend)
{
    public Task<VirtualPortSnapshot> Refresh(CancellationToken ct = default) => backend.Read(ct);
    public async Task<VirtualPortPair> Create(string a, string b, CancellationToken ct = default)
    {
        var before = await backend.Read(ct);
        string[] arguments = VirtualSerial.CreateArguments(a, b, before.UnavailableNames);
        if (before.DeviceIssues.Values.FirstOrDefault(issue => issue.Code is 48 or 52) is { } blocked)
            throw new IOException(blocked.Message + " 管理员运行不能解除驱动签名或兼容性拦截，请先更换兼容驱动，再创建端口对。");
        ct.ThrowIfCancellationRequested();
        int code = await backend.Change(arguments);
        var after = await backend.Read(CancellationToken.None);
        var pair = after.Pairs.SingleOrDefault(p => p.PortA == VirtualSerial.PortName(a) && p.PortB == VirtualSerial.PortName(b));
        if (pair is null || !after.IsPresent(pair))
        {
            string reason = pair is not null && after.DeviceIssues.TryGetValue(pair.Id, out var issue)
                ? issue.Message : "尚未检测到两个可用 COM 口，请检查设备管理器及是否需要重启。";
            throw new IOException($"创建未就绪（setupc 退出码 {code}）：{reason} 已保留设备检测结果，请勿重复创建。");
        }
        return pair;
    }
    public async Task Remove(VirtualPortPair expected, CancellationToken ct = default)
    {
        var current = await backend.Read(ct);
        string[] arguments = VirtualSerial.RemoveArguments(expected, current);
        ct.ThrowIfCancellationRequested();
        int code = await backend.Change(arguments);
        var after = await backend.Read(CancellationToken.None);
        if (after.Pairs.Any(p => p.Id == expected.Id) || after.PresentPorts.Any(p => p.Equals(expected.PortA, StringComparison.OrdinalIgnoreCase) || p.Equals(expected.PortB, StringComparison.OrdinalIgnoreCase)))
            throw new IOException($"端口仍然存在（setupc 退出码 {code}）。请关闭占用端口的软件，再刷新检查；可能需要重启。");
    }
}
