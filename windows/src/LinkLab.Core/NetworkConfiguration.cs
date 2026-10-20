using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace LinkLab.Core;

public sealed record NetworkInterfaceChoice(string Address, string Mask, string Name)
{
    public string Display => $"{Name}  ·  {Address}";
    public string Broadcast => Address == "0.0.0.0" ? "255.255.255.255" : NetworkConfiguration.Broadcast(Address, Mask);
}

public sealed record NetworkDeviceSettings(
    string Name, string Address, string Mask, string Gateway, int LocalPort,
    string Destination, int DestinationPort, int Mode, int Baud, int Parity,
    bool Dhcp, int DataBits, int StopBits, int FlowControl, int AppProtocol = 0);

public sealed record NetworkDeviceSnapshot(string Identity, NetworkDeviceSettings Settings,
    byte[] Parameters, IPEndPoint Peer, DateTimeOffset Seen)
{
    public string Name => Settings.Name;
    public string Address => Settings.Address;
    public int Port => Settings.LocalPort;
    public string Mode => NetworkConfiguration.Modes.ElementAtOrDefault(Settings.Mode) ?? $"模式 {Settings.Mode}";
    public string LastSeen => Seen.ToLocalTime().ToString("HH:mm:ss");
    public bool Writable => Parameters.Length == ZlanConfigurationProtocol.ParameterLength;
    public bool SupportsDns => Parameters.Length > 104 && (Parameters[104] & 2) != 0;
    public bool SupportsDhcp => Parameters.Length > 104 && (Parameters[104] & 32) != 0;
    public bool PasswordRequired => Parameters.Length > 110 && (Parameters[110] & 4) != 0;
    public string ProtocolState => Writable ? "可编辑" : $"只读 · {Parameters.Length} 字节参数";
}

public interface INetworkConfigurationBackend
{
    event Action<WireEvent>? Traffic;
    Task<IReadOnlyList<NetworkDeviceSnapshot>> Discover(string bindAddress, string target, bool broadcast, int timeout, CancellationToken ct);
    Task<NetworkDeviceSnapshot> Read(string bindAddress, NetworkDeviceSnapshot expected, int timeout, CancellationToken ct);
    Task<NetworkDeviceSnapshot> Apply(string bindAddress, NetworkDeviceSnapshot expected, NetworkDeviceSettings settings, string password, int timeout, CancellationToken ct);
}

public static class NetworkConfiguration
{
    public static string[] Modes { get; } = ["TCP Server", "TCP Client", "UDP", "UDP Multicast"];
    public static int[] BaudRates { get; } = [1200, 2400, 4800, 7200, 9600, 14400, 19200, 28800, 38400, 57600, 76800, 115200, 230400, 460800];
    public static string[] Parities { get; } = ["None", "Even", "Odd", "Mark", "Space"];
    public static string[] AppProtocols { get; } = ["透明传输", "Modbus TCP ↔ RTU", "RealCom"];

    public static IReadOnlyList<NetworkInterfaceChoice> Interfaces()
    {
        var result = new List<NetworkInterfaceChoice> { new("0.0.0.0", "0.0.0.0", "自动选择网卡") };
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces().Where(nic => nic.OperationalStatus == OperationalStatus.Up))
        {
            try
            {
                foreach (var unicast in nic.GetIPProperties().UnicastAddresses.Where(x => x.Address.AddressFamily == AddressFamily.InterNetwork))
                    result.Add(new(unicast.Address.ToString(), unicast.IPv4Mask.ToString(), nic.Name));
            }
            catch (NetworkInformationException) { }
        }
        if (!result.Any(nic => nic.Address == "127.0.0.1")) result.Add(new("127.0.0.1", "255.0.0.0", "本机回环"));
        return result.DistinctBy(nic => nic.Address).ToArray();
    }

    private static byte[] Ipv4Bytes(string text, string label)
    {
        var parts = text.Split('.');
        if (parts.Length != 4 || parts.Any(part => part.Length == 0 || (part.Length > 1 && part[0] == '0') || part.Any(ch => ch is < '0' or > '9') || !byte.TryParse(part, out _)))
            throw new ArgumentException($"{label}必须是完整的 IPv4 地址。");
        return parts.Select(byte.Parse).ToArray();
    }
    public static IPAddress Ipv4(string text, string label, bool allowZero = false, bool allowBroadcast = false)
    {
        var bytes = Ipv4Bytes(text, label); var address = new IPAddress(bytes);
        bool isBroadcast = bytes.All(value => value == 255);
        if ((!allowZero && bytes.All(value => value == 0)) || (bytes[0] == 0 && bytes.Any(value => value != 0)) || (!allowBroadcast && isBroadcast) || (bytes[0] >= 224 && !(allowBroadcast && isBroadcast)))
            throw new ArgumentException($"{label}必须是单播 IPv4 地址。");
        return address;
    }
    public static IPAddress Ipv4Literal(string text, string label) => new(Ipv4Bytes(text, label));

    public static uint MaskValue(string mask)
    {
        var bytes = Ipv4Bytes(mask, "子网掩码");
        uint value = ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
        uint inverse = ~value;
        if ((inverse & (inverse + 1)) != 0) throw new ArgumentException("子网掩码必须由连续的 1 和 0 组成。");
        return value;
    }

    public static string Broadcast(string address, string mask)
    {
        var ip = Ipv4(address, "本机 IP").GetAddressBytes(); var subnet = Ipv4Bytes(mask, "子网掩码"); _ = MaskValue(mask);
        return new IPAddress(ip.Select((value, i) => (byte)(value | ~subnet[i])).ToArray()).ToString();
    }

    public static void Validate(NetworkDeviceSettings settings)
    {
        if (settings.Name.Length > 9 || settings.Name.Any(ch => ch is < ' ' or > '~')) throw new ArgumentException("设备名称最多 9 个 ASCII 可见字符。");
        var ip = Ipv4(settings.Address, "设备 IP").GetAddressBytes();
        uint mask = MaskValue(settings.Mask);
        if (mask == 0) throw new ArgumentException("子网掩码不能为 0.0.0.0。");
        uint addressValue = ((uint)ip[0] << 24) | ((uint)ip[1] << 16) | ((uint)ip[2] << 8) | ip[3];
        uint hostMask = ~mask;
        if (!settings.Dhcp && hostMask > 1 && ((addressValue & hostMask) == 0 || (addressValue & hostMask) == hostMask)) throw new ArgumentException("静态设备 IP 不能是该子网的网络地址或广播地址。");
        var gateway = Ipv4(settings.Gateway, "网关", true).GetAddressBytes();
        var subnet = Ipv4Bytes(settings.Mask, "子网掩码");
        if (gateway.Any(value => value != 0) && Enumerable.Range(0, 4).Any(i => (ip[i] & subnet[i]) != (gateway[i] & subnet[i]))) throw new ArgumentException("网关必须与设备 IP 位于同一子网，或设置为 0.0.0.0。");
        if (settings.LocalPort is < 1 or > 65535 || settings.DestinationPort is < 1 or > 65535) throw new ArgumentException("网络端口范围为 1–65535。");
        if (settings.Mode is < 0 or > 3 || settings.Parity is < 0 or > 4 || !BaudRates.Contains(settings.Baud)) throw new ArgumentException("工作模式、校验或波特率不受当前协议支持。");
        if (settings.DataBits is < 5 or > 8 || settings.StopBits is < 1 or > 2 || settings.FlowControl is < 0 or > 3) throw new ArgumentException("串口格式不受当前协议支持。");
        if (settings.AppProtocol is < 0 or > 2) throw new ArgumentException("转化协议不受当前管理协议支持。");
        if (settings.Destination.Length is < 1 or > 29 || settings.Destination.Any(ch => ch is <= ' ' or > '~')) throw new ArgumentException("目标地址必须为 IPv4 或不超过 29 个 ASCII 字符的域名。");
        if (settings.Destination.All(ch => char.IsDigit(ch) || ch == '.'))
        {
            // UDP multicast is the only mode that accepts a multicast destination.
            var destination = Ipv4Literal(settings.Destination, "目标 IPv4");
            if (settings.Mode != 3 && destination.GetAddressBytes()[0] is >= 224 and <= 239) throw new ArgumentException("组播目标地址需要选择 UDP Multicast 模式。");
        }
        else if (Uri.CheckHostName(settings.Destination) != UriHostNameType.Dns) throw new ArgumentException("目标域名格式不正确。");
    }
}
