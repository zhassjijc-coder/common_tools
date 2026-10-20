using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace LinkLab.Core;

// Independently implemented from Shanghai ZLAN's published UDP management-port
// specification, Rev.4. https://www.zlmcu.com/download/UDP_port_protocol.pdf
// The complete original parameter block is retained so unrelated device features
// (including the variable extension area) are never reconstructed from defaults.
public static class ZlanConfigurationProtocol
{
    public const int ParameterLength = 167;
    public const int PacketLength = ParameterLength + 3;
    public const int ManagementPort = 1092;
    public static byte[] Query(bool broadcast)
    {
        var packet = new byte[PacketLength]; packet[0] = 0x5A; packet[1] = 0x4C; packet[2] = broadcast ? (byte)0 : (byte)4; return packet;
    }
    private static string Text(ReadOnlySpan<byte> bytes)
    {
        int end = bytes.IndexOf((byte)0); if (end >= 0) bytes = bytes[..end];
        return Encoding.ASCII.GetString(bytes).TrimEnd();
    }
    private static string Address(ReadOnlySpan<byte> bytes, int offset) => new IPAddress(bytes.Slice(offset, 4)).ToString();
    public static NetworkDeviceSnapshot ParseResponse(ReadOnlySpan<byte> packet, IPEndPoint peer)
    {
        if (packet.Length is < 118 or > 4096 || packet[0] != 0x5A || packet[1] != 0x4C || packet[2] != 1) throw new FormatException("不是受支持的卓岚设备参数应答。");
        var p = packet[3..]; var id = p.Slice(31, 6);
        if (id.ToArray().All(value => value == 0) || id.ToArray().All(value => value == 255)) throw new FormatException("设备应答缺少有效 DevID。");
        string identity = string.Join(":", id.ToArray().Select(value => value.ToString("X2")));
        if (p[100] != 0) identity += $" / P{p[100]}";
        string destination = (p[104] & 2) != 0 ? Text(p.Slice(66, 30)) : "";
        if (string.IsNullOrWhiteSpace(destination)) destination = Address(p, 12);
        if (p[20] == 3) destination = Address(p, 105);
        var settings = new NetworkDeviceSettings(Text(p.Slice(38, 10)), Address(p, 0), Address(p, 4), Address(p, 8),
            BinaryPrimitives.ReadUInt16BigEndian(p[16..]), destination, BinaryPrimitives.ReadUInt16BigEndian(p[18..]), p[20],
            p[37] < NetworkConfiguration.BaudRates.Length ? NetworkConfiguration.BaudRates[p[37]] : 0,
            p[48], p[56] != 0, 8 - p[59], (p[114] & 1) != 0 ? 2 : 1, p[57], p[60]);
        return new(identity, settings, p.ToArray(), peer, DateTimeOffset.Now);
    }
    private static void SetAddress(byte[] parameters, int offset, string address) => NetworkConfiguration.Ipv4Literal(address, "IPv4").GetAddressBytes().CopyTo(parameters, offset);
    private static void SetText(byte[] parameters, int offset, int size, string value)
    {
        parameters.AsSpan(offset, size).Clear(); Encoding.ASCII.GetBytes(value).CopyTo(parameters, offset);
    }
    public static byte[] Write(NetworkDeviceSnapshot original, NetworkDeviceSettings settings, string password = "")
    {
        NetworkConfiguration.Validate(settings);
        if (!original.Writable) throw new InvalidOperationException("此固件的参数长度与已验证的 167 字节格式不同，仅支持读取，不能写入。");
        var originalFrame = new byte[PacketLength]; originalFrame[0] = 0x5A; originalFrame[1] = 0x4C; originalFrame[2] = 1; original.Parameters.CopyTo(originalFrame, 3);
        var parsedOriginal = ParseResponse(originalFrame, original.Peer);
        if (parsedOriginal.Identity != original.Identity || parsedOriginal.Settings != original.Settings) throw new InvalidOperationException("设备快照与原始参数不匹配，拒绝写入。");
        if (settings.Dhcp && !original.Settings.Dhcp && !original.SupportsDhcp) throw new InvalidOperationException("此设备未声明支持 DHCP，不能启用自动获取 IP。");
        bool destinationIsIp = settings.Destination.All(ch => char.IsDigit(ch) || ch == '.');
        var destination = destinationIsIp ? NetworkConfiguration.Ipv4Literal(settings.Destination, "目标 IPv4") : null;
        if (!destinationIsIp && !original.SupportsDns) throw new InvalidOperationException("此设备未声明支持 DNS，目标地址必须填写 IPv4。");
        if (settings.Mode == 3 && (settings.Mode != original.Settings.Mode || settings.Destination != original.Settings.Destination) && ((original.Parameters[112] & 2) == 0 || !destinationIsIp || destination!.GetAddressBytes()[0] is < 224 or > 239)) throw new InvalidOperationException("UDP Multicast 需要设备声明支持组播，且目标地址为 224.0.0.0–239.255.255.255。");
        if (settings.AppProtocol != original.Settings.AppProtocol && ((settings.AppProtocol == 1 && (original.Parameters[104] & 8) == 0) || (settings.AppProtocol == 2 && (original.Parameters[104] & 4) == 0))) throw new InvalidOperationException("此设备未声明支持所选转化协议，请选择设备支持的模式。");
        var parameters = original.Parameters.ToArray();
        SetAddress(parameters, 0, settings.Address); SetAddress(parameters, 4, settings.Mask); SetAddress(parameters, 8, settings.Gateway);
        if (settings.Destination != original.Settings.Destination || settings.Mode != original.Settings.Mode)
        {
            if (settings.Mode == 3) SetAddress(parameters, 105, settings.Destination);
            else
            {
                if (destinationIsIp) SetAddress(parameters, 12, settings.Destination);
                if (original.SupportsDns) SetText(parameters, 66, 30, settings.Destination);
            }
        }
        BinaryPrimitives.WriteUInt16BigEndian(parameters.AsSpan(16), checked((ushort)settings.LocalPort));
        BinaryPrimitives.WriteUInt16BigEndian(parameters.AsSpan(18), checked((ushort)settings.DestinationPort));
        parameters[20] = checked((byte)settings.Mode); parameters[37] = checked((byte)Array.IndexOf(NetworkConfiguration.BaudRates, settings.Baud));
        if (settings.Name != original.Settings.Name) SetText(parameters, 38, 10, settings.Name);
        parameters[48] = checked((byte)settings.Parity);
        if (settings.Dhcp != original.Settings.Dhcp) parameters[56] = settings.Dhcp ? (byte)1 : (byte)0;
        parameters[57] = checked((byte)settings.FlowControl); parameters[59] = checked((byte)(8 - settings.DataBits));
        parameters[60] = checked((byte)settings.AppProtocol);
        parameters[114] = (byte)((parameters[114] & ~1) | (settings.StopBits == 2 ? 1 : 0));
        if (original.PasswordRequired)
        {
            if (password.Length is < 1 or > 9 || password.Any(ch => ch is < ' ' or > '~')) throw new ArgumentException("此设备启用了配置密码，请填写 1–9 个 ASCII 可见字符的设备密码。");
            SetText(parameters, 21, 10, password);
        }
        var packet = new byte[PacketLength]; packet[0] = 0x5A; packet[1] = 0x4C; packet[2] = 2; parameters.CopyTo(packet, 3); return packet;
    }
    public static byte[] Redacted(ReadOnlySpan<byte> packet)
    {
        var safe = packet.ToArray();
        if (safe.Length >= 34) safe.AsSpan(24, 10).Clear(); // Device authentication area.
        if (safe.Length > 118) safe.AsSpan(118).Clear(); // Variable extensions may contain Wi-Fi secrets.
        return safe;
    }
    public static bool Matches(NetworkDeviceSnapshot readback, NetworkDeviceSnapshot expected, NetworkDeviceSettings requested) =>
        readback.Identity == expected.Identity && (requested.Dhcp
            ? readback.Settings with { Address = requested.Address, Mask = requested.Mask, Gateway = requested.Gateway } == requested
            : readback.Settings == requested);
}

public sealed class ZlanNetworkConfigurationBackend(int managementPort = ZlanConfigurationProtocol.ManagementPort) : INetworkConfigurationBackend
{
    public event Action<WireEvent>? Traffic;
    private void Record(string direction, byte[] packet, IPEndPoint peer) => Traffic?.Invoke(new(DateTimeOffset.Now, "网口配置", direction,
        ZlanConfigurationProtocol.Redacted(packet), peer.ToString(), "卓岚 UDP 管理协议 · 认证及扩展字段已隐藏"));
    private static UdpClient Socket(string bindAddress, bool broadcast = false)
    {
        var local = NetworkConfiguration.Ipv4(bindAddress, "本机 IP", true);
        // Cross-subnet devices reply to 255.255.255.255. Binding a discovery
        // socket to one unicast IP can lose those responses, so listen on ANY
        // and select the outgoing Windows interface with IP_UNICAST_IF (31).
        var socket = new UdpClient(new IPEndPoint(broadcast ? IPAddress.Any : local, 0));
        try
        {
            socket.EnableBroadcast = true;
            if (broadcast && !local.Equals(IPAddress.Any) && !IPAddress.IsLoopback(local) && OperatingSystem.IsWindows())
            {
                var nic = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(nic => nic.GetIPProperties().UnicastAddresses.Any(address => address.Address.Equals(local)));
                if (nic is null) throw new InvalidOperationException("所选本机网卡已断开，请刷新网卡后重试。");
                int index = nic.GetIPProperties().GetIPv4Properties().Index;
                socket.Client.SetSocketOption(SocketOptionLevel.IP, (SocketOptionName)31, IPAddress.HostToNetworkOrder(index));
            }
            return socket;
        }
        catch { socket.Dispose(); throw; }
    }
    public async Task<IReadOnlyList<NetworkDeviceSnapshot>> Discover(string bindAddress, string target, bool broadcast, int timeout, CancellationToken ct)
    {
        if (timeout is < 100 or > 30000) throw new ArgumentOutOfRangeException(nameof(timeout));
        ct.ThrowIfCancellationRequested();
        var peer = new IPEndPoint(NetworkConfiguration.Ipv4(target, "目标 IP", false, broadcast), managementPort);
        using var socket = Socket(bindAddress, broadcast); using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(timeout);
        var packet = ZlanConfigurationProtocol.Query(broadcast); await socket.SendAsync(packet, peer, ct); Record("TX", packet, peer);
        var devices = new Dictionary<string, NetworkDeviceSnapshot>();
        while (devices.Count < 256)
        {
            UdpReceiveResult reply;
            try { reply = await socket.ReceiveAsync(deadline.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { break; }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionRefused or SocketError.HostUnreachable or SocketError.NetworkUnreachable) { continue; }
            if ((!broadcast && !reply.RemoteEndPoint.Address.Equals(peer.Address)) || reply.RemoteEndPoint.Port != managementPort) continue;
            try
            {
                var device = ZlanConfigurationProtocol.ParseResponse(reply.Buffer, reply.RemoteEndPoint);
                devices[device.Identity] = device; Record("RX", reply.Buffer, reply.RemoteEndPoint);
            }
            catch (FormatException) { }
        }
        ct.ThrowIfCancellationRequested(); return devices.Values.OrderBy(device => device.Address).ThenBy(device => device.Identity).ToArray();
    }
    public async Task<NetworkDeviceSnapshot> Read(string bindAddress, NetworkDeviceSnapshot expected, int timeout, CancellationToken ct)
    {
        var devices = await Discover(bindAddress, expected.Peer.Address.ToString(), false, timeout, ct);
        return devices.FirstOrDefault(device => device.Identity == expected.Identity) ?? throw new TimeoutException("未收到目标 DevID 的设备应答。设备可能离线或 IP 已变化，请重新搜索。");
    }
    public async Task<NetworkDeviceSnapshot> Apply(string bindAddress, NetworkDeviceSnapshot expected, NetworkDeviceSettings settings, string password, int timeout, CancellationToken ct)
    {
        var current = await Read(bindAddress, expected, timeout, ct);
        if (current.Settings != expected.Settings) throw new InvalidOperationException("设备参数已被其他操作修改，请重新读取并检查修改。");
        var packet = ZlanConfigurationProtocol.Write(current, settings, password);
        ct.ThrowIfCancellationRequested();
        using (var socket = Socket(bindAddress)) { await socket.SendAsync(packet, new IPEndPoint(current.Peer.Address, managementPort), ct); Record("TX", packet, current.Peer); }
        // The documented 0x02 command restarts the device and defines no write ACK.
        // Only a subsequent 0x01 response with the same DevID and requested fields
        // confirms success. Never automatically resend 0x02 after a timeout.
        var candidates = new[] { settings.Address, current.Peer.Address.ToString() }.Distinct().ToArray();
        var end = DateTime.UtcNow.AddMilliseconds(Math.Max(5000, timeout * 3));
        while (DateTime.UtcNow < end)
        {
            await Task.Delay(350, ct);
            foreach (string candidate in candidates)
            {
                IReadOnlyList<NetworkDeviceSnapshot> responses;
                try { responses = await Discover(bindAddress, candidate, false, Math.Min(timeout, 800), ct); }
                catch (SocketException ex) when (ex.SocketErrorCode is SocketError.HostUnreachable or SocketError.NetworkUnreachable or SocketError.AddressNotAvailable) { continue; }
                var readback = responses.FirstOrDefault(device => device.Identity == current.Identity);
                if (readback is not null && ZlanConfigurationProtocol.Matches(readback, current, settings)) return readback;
            }
            if (settings.Dhcp)
            {
                // A DHCP server can assign a completely new address. The explicit
                // write operation authorizes a read-only rediscovery for that DevID.
                var responses = await Discover(bindAddress, "255.255.255.255", true, Math.Min(timeout, 800), ct);
                var readback = responses.FirstOrDefault(device => device.Identity == current.Identity);
                if (readback is not null && ZlanConfigurationProtocol.Matches(readback, current, settings)) return readback;
            }
        }
        throw new TimeoutException("配置报文已发送，但未能回读同一设备并确认参数。设备可能仍在重启、IP 已改变或拒绝了密码；请重新搜索确认，勿连续重复写入。");
    }
}
