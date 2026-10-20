using System.ComponentModel;
using System.IO;
using System.IO.Ports;
using System.Runtime.InteropServices;
using System.Text;
using LinkLab.Core;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace LinkLab.App;

// Read Windows device metadata instead of executing setupc: some distributions mark even
// `setupc list` as requireAdministrator. These handles are read-only and never install devices.
internal static class VirtualPortDevices
{
    private const string Parameters = @"SYSTEM\CurrentControlSet\Services\com0com\Parameters";
    private static readonly IntPtr InvalidHandle = new(-1);

    internal static VirtualPortSnapshot Read(CancellationToken ct)
    {
        var buses = new SortedSet<int>();
        var issues = new Dictionary<int, VirtualPortIssue>();
        var realNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        IntPtr devices = SetupDiGetClassDevsW(IntPtr.Zero, null, IntPtr.Zero, 4); // DIGCF_ALLCLASSES, including unready devices
        if (devices == InvalidHandle) throw DeviceError("无法读取 Windows 设备列表");
        try
        {
            for (uint i = 0; ; i++)
            {
                ct.ThrowIfCancellationRequested();
                var data = new DeviceInfo { Size = Marshal.SizeOf<DeviceInfo>() };
                if (!SetupDiEnumDeviceInfo(devices, i, ref data))
                {
                    if (Marshal.GetLastWin32Error() == 259) break; // ERROR_NO_MORE_ITEMS
                    throw DeviceError("枚举 Windows 设备失败");
                }
                string[] ids = Property(devices, ref data, 1).Split('\0', StringSplitOptions.RemoveEmptyEntries); // SPDRP_HARDWAREID
                bool bus = ids.Contains(@"root\com0com", StringComparer.OrdinalIgnoreCase);
                bool port = ids.Contains(@"com0com\port", StringComparer.OrdinalIgnoreCase);
                if (!bus && !port) continue;
                IntPtr raw = SetupDiOpenDevRegKey(devices, ref data, 1, 0, 1, 0x20019); // GLOBAL, DIREG_DEV, KEY_READ
                if (raw == InvalidHandle) throw DeviceError("无法读取 com0com 设备参数，请检查设备安装状态");
                using var handle = new SafeRegistryHandle(raw, true);
                using var key = RegistryKey.FromHandle(handle);
                if (bus)
                {
                    if (key.GetValue("PortNum") is not int number || number is < 0 or > 999999 || !buses.Add(number))
                        throw new IOException("com0com 设备编号缺失或重复，请在设备管理器中检查驱动；已停止端口管理操作。");
                    if (CM_Get_DevNode_Status(out _, out uint problem, data.DevInst, 0) == 0 && problem != 0)
                        issues[number] = new(problem, ProblemMessage(problem));
                    else if (string.IsNullOrEmpty(Property(devices, ref data, 4).TrimEnd('\0')))
                        issues[number] = new(28, "设备记录尚未绑定驱动，不能用于串口通信。");
                }
                else
                {
                    string deviceName = Property(devices, ref data, 14).TrimEnd('\0'); // SPDRP_PHYSICAL_DEVICE_OBJECT_NAME
                    if (key.GetValue("PortName") is string name && !string.IsNullOrEmpty(deviceName))
                        if (!realNames.TryAdd(deviceName, name)) throw new IOException("com0com 端口设备重复，请检查驱动。");
                }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(devices); }

        var listing = new StringBuilder();
        using var parameters = Registry.LocalMachine.OpenSubKey(Parameters);
        foreach (int id in buses)
        {
            ct.ThrowIfCancellationRequested();
            foreach (char side in new[] { 'A', 'B' })
            {
                string physicalName = $"CNC{side}{id}";
                using var key = parameters?.OpenSubKey(physicalName);
                string configured = key?.GetValue("PortName") as string ?? physicalName;
                string deviceName = @"\Device\com0com" + (side == 'A' ? "1" : "2") + id;
                string name = ResolveName(physicalName, configured, realNames.GetValueOrDefault(deviceName));
                listing.AppendLine($"{physicalName} PortName={name}");
            }
        }
        var reserved = new List<string>();
        using var comDb = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\COM Name Arbiter");
        if (comDb?.GetValue("ComDB") is byte[] bytes)
            for (int n = 0; n < Math.Min(4096, bytes.Length * 8); n++)
                if ((bytes[n / 8] & (1 << (n % 8))) != 0) reserved.Add($"COM{n + 1}");
        return new(listing.ToString(), SerialPort.GetPortNames(), reserved.ToArray()) { DeviceIssues = issues };
    }

    internal static string ProblemMessage(uint code) => code switch
    {
        52 => "Windows 无法验证 com0com 驱动的数字签名（代码 52），已阻止驱动加载。",
        28 => "com0com 驱动尚未安装完成（代码 28）。",
        10 => "com0com 设备无法启动（代码 10），请检查驱动兼容性。",
        14 => "com0com 驱动需要重启 Windows 后生效（代码 14）。",
        22 => "com0com 设备已被禁用（代码 22），请在设备管理器中检查。",
        48 => "Windows 已阻止加载此 com0com 驱动（代码 48），请更换兼容版本。",
        _ => $"com0com 设备未就绪（Windows 代码 {code}），请在设备管理器中检查。"
    };

    internal static string ResolveName(string physical, string configured, string? real)
    {
        string name = configured.Equals("COM#", StringComparison.OrdinalIgnoreCase) ? real ?? "COM#" : configured;
        // Do not let malformed registry data inject extra listing lines or a different pair ID.
        if (name.Length is < 1 or > 64 || name.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '_' and not '-' and not '#'))
            throw new IOException($"com0com 端口 {physical} 的名称无效，请通过驱动工具检查配置。");
        return name.ToUpperInvariant();
    }

    private static string Property(IntPtr devices, ref DeviceInfo data, uint property)
    {
        byte[] buffer = new byte[1024];
        if (!SetupDiGetDeviceRegistryPropertyW(devices, ref data, property, out _, buffer, (uint)buffer.Length, out uint required))
        {
            int error = Marshal.GetLastWin32Error();
            if (error == 13) return ""; // ERROR_INVALID_DATA: optional property is absent
            if (error != 122 || required > 65536) throw DeviceError("读取设备属性失败", error);
            buffer = new byte[required];
            if (!SetupDiGetDeviceRegistryPropertyW(devices, ref data, property, out _, buffer, required, out required))
                throw DeviceError("读取设备属性失败");
        }
        return Encoding.Unicode.GetString(buffer, 0, (int)required);
    }

    private static IOException DeviceError(string text, int? code = null)
    {
        var error = new Win32Exception(code ?? Marshal.GetLastWin32Error());
        return new IOException($"{text}（{error.NativeErrorCode}）：{error.Message}", error);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInfo { public int Size; public Guid ClassGuid; public uint DevInst; public IntPtr Reserved; }
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsW(IntPtr classGuid, string? enumerator, IntPtr hwnd, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr devices, uint index, ref DeviceInfo data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceRegistryPropertyW(IntPtr devices, ref DeviceInfo data, uint property, out uint type, byte[] buffer, uint size, out uint required);
    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiOpenDevRegKey(IntPtr devices, ref DeviceInfo data, uint scope, uint profile, uint keyType, uint access);
    [DllImport("setupapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr devices);
    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_DevNode_Status(out uint status, out uint problem, uint devInst, uint flags);
}
