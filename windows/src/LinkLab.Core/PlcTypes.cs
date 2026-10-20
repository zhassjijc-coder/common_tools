using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LinkLab.Core;

public enum PlcProtocol { SiemensS7, MitsubishiMc3E, OmronFinsTcp }
public enum PlcDataType { UInt16, Int16, UInt32, Int32, Float32, Bool }

public sealed record PlcOptions(PlcProtocol Protocol, string Host, int Port, byte Rack = 0, byte Slot = 1,
    byte ClientNode = 0, byte ServerNode = 0, byte Network = 0, byte Station = 255, ushort IoNumber = 1023,
    byte Unit = 0, byte SourceNetwork = 0, ushort LocalTsap = 0x0100, ushort RemoteTsap = 0);

public sealed record PlcAddress(PlcProtocol Protocol, byte Area, ushort Block, int Index, int Bit, bool IsBit, string Prefix)
{
    public string Bank => $"{Protocol}/{Area}/{Block}";
    public bool McBit => Protocol == PlcProtocol.MitsubishiMc3E && Area is 0x90 or 0x9C or 0x9D or 0x92 or 0xA0;
    public int BankSize => Protocol == PlcProtocol.SiemensS7 || McBit ? 65536 : 131072;
    public int ByteOffset => Protocol == PlcProtocol.SiemensS7 || McBit ? Index : checked(Index * 2);
    public string Display => Protocol == PlcProtocol.SiemensS7
        ? $"{(Area == 0x84 ? $"DB{Block}." : Prefix)}{Index}{(IsBit ? $".{Bit}" : "")}" :
        $"{Prefix}{(Protocol == PlcProtocol.MitsubishiMc3E && Prefix is "X" or "Y" or "B" or "W" ? Index.ToString("X") : Index.ToString())}{(IsBit && !McBit ? $".{Bit}" : "")}";
    public PlcAddress Advance(int values, PlcDataType type)
    {
        if (IsBit && !McBit)
        {
            int width = Protocol == PlcProtocol.SiemensS7 ? 8 : 16;
            return this with { Index = Index + (Bit + values) / width, Bit = (Bit + values) % width };
        }
        return this with { Index = Index + values * (McBit ? 1 : PlcValues.Size(type) / (Protocol == PlcProtocol.SiemensS7 ? 1 : 2)) };
    }
    public static PlcAddress Parse(PlcProtocol protocol, string text, PlcDataType type)
    {
        string input = text.Trim().ToUpperInvariant(); bool bit = type == PlcDataType.Bool;
        if (protocol == PlcProtocol.SiemensS7)
        {
            var db = Regex.Match(input, @"^DB(\d+)\.(?:DB([BWD X]))?(\d+)(?:\.(\d))?$".Replace(" ", ""));
            var other = Regex.Match(input, @"^([MIQ])([BWD]?)(\d+)(?:\.(\d))?$");
            var m = db.Success ? db : other;
            if (!m.Success) throw new ArgumentException("S7 地址示例：DB1.0、DB1.DBW0、M0；Bool 使用 DB1.0.0 / M0.0。");
            ushort block = db.Success ? ushort.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : (ushort)0;
            if (db.Success && block == 0) throw new ArgumentException("DB 编号为 1–65535。");
            byte area = db.Success ? (byte)0x84 : m.Groups[1].Value switch { "M" => (byte)0x83, "I" => (byte)0x81, _ => (byte)0x82 };
            int index = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
            string suffix = m.Groups[2].Value;
            if ((suffix == "W" && PlcValues.Size(type) != 2) || (suffix == "D" && PlcValues.Size(type) != 4) || suffix == "B" || (suffix == "X" && !bit))
                throw new ArgumentException("地址中的 B/W/D/X 与所选类型不匹配；可用 DB1.0 这样的字节偏移地址。");
            if (bit != m.Groups[4].Success) throw new ArgumentException("Bool 地址必须带 .0–.7 位号；数值地址不能带位号。");
            int b = bit ? int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture) : 0;
            if (index > 65535 || b > 7) throw new ArgumentException("字节地址范围 0–65535，位号 0–7。");
            return new(protocol, area, block, index, b, bit, db.Success ? "DB" : m.Groups[1].Value);
        }
        var match = Regex.Match(input, protocol == PlcProtocol.MitsubishiMc3E ? @"^(D|M|X|Y|W|R|L|B)([0-9A-F]+)$" : @"^(CIO|D|W|H|A)(\d+)(?:\.(\d{1,2}))?$");
        if (!match.Success) throw new ArgumentException(protocol == PlcProtocol.MitsubishiMc3E ? "MC 地址示例：D100 / W100；Bool 使用 M100 / X10 / Y10 / L0 / B0。" : "FINS 地址示例：D100 / CIO0 / W0 / H0 / A0；Bool 使用 D100.0。");
        string prefix = match.Groups[1].Value;
        bool hex = protocol == PlcProtocol.MitsubishiMc3E && prefix is "X" or "Y" or "W" or "B";
        int address = int.Parse(match.Groups[2].Value, hex ? NumberStyles.HexNumber : NumberStyles.None, CultureInfo.InvariantCulture);
        if (address > 65535) throw new ArgumentException("当前支持的设备地址范围为 0–65535。");
        byte code = (byte)(protocol == PlcProtocol.MitsubishiMc3E ? prefix switch { "D" => 0xA8, "M" => 0x90, "X" => 0x9C, "Y" => 0x9D, "W" => 0xB4, "R" => 0xAF, "L" => 0x92, _ => 0xA0 }
            : prefix switch { "D" => 0x82, "CIO" => 0xB0, "W" => 0xB1, "H" => 0xB2, _ => 0xB3 });
        int bitIndex = match.Groups.Count > 3 && match.Groups[3].Success ? int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture) : 0;
        if (protocol == PlcProtocol.MitsubishiMc3E)
        { if (bit != (prefix is "M" or "X" or "Y" or "L" or "B")) throw new ArgumentException("M/X/Y/L/B 使用 Bool；D/W/R 使用数值类型。"); }
        else if (bit != match.Groups[3].Success || bitIndex > 15) throw new ArgumentException("FINS Bool 必须带 .0–.15 位号；数值地址不能带位号。");
        return new(protocol, code, 0, address, bitIndex, bit, prefix);
    }
}

public static class PlcValues
{
    public static int Size(PlcDataType type) => type switch { PlcDataType.Bool => 1, PlcDataType.Int16 or PlcDataType.UInt16 => 2, _ => 4 };
    public static WordOrder DefaultOrder(PlcProtocol protocol) => protocol switch { PlcProtocol.SiemensS7 => WordOrder.ABCD, PlcProtocol.MitsubishiMc3E => WordOrder.DCBA, _ => WordOrder.CDAB };
    private static byte[] Reorder(byte[] bytes, WordOrder order)
    {
        if (bytes.Length == 1) return bytes;
        if (order is WordOrder.BADC or WordOrder.DCBA) for (int i = 0; i < bytes.Length; i += 2) (bytes[i], bytes[i + 1]) = (bytes[i + 1], bytes[i]);
        if (bytes.Length == 4 && order is WordOrder.CDAB or WordOrder.DCBA) { (bytes[0], bytes[2]) = (bytes[2], bytes[0]); (bytes[1], bytes[3]) = (bytes[3], bytes[1]); }
        return bytes;
    }
    public static byte[] Encode(string value, PlcDataType type, WordOrder order)
    {
        var b = new byte[Size(type)]; var ci = CultureInfo.InvariantCulture;
        switch (type)
        {
            case PlcDataType.Bool: b[0] = value.Trim().ToLowerInvariant() switch { "1" or "true" or "on" => 1, "0" or "false" or "off" => 0, _ => throw new FormatException("Bool 输入 0/1、true/false 或 on/off。") }; break;
            case PlcDataType.Int16: BinaryPrimitives.WriteInt16BigEndian(b, short.Parse(value, ci)); break;
            case PlcDataType.UInt16: BinaryPrimitives.WriteUInt16BigEndian(b, value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? Convert.ToUInt16(value[2..], 16) : ushort.Parse(value, ci)); break;
            case PlcDataType.Int32: BinaryPrimitives.WriteInt32BigEndian(b, int.Parse(value, ci)); break;
            case PlcDataType.UInt32: BinaryPrimitives.WriteUInt32BigEndian(b, value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? Convert.ToUInt32(value[2..], 16) : uint.Parse(value, ci)); break;
            case PlcDataType.Float32: BinaryPrimitives.WriteSingleBigEndian(b, float.Parse(value, ci)); break;
        }
        return Reorder(b, order);
    }
    public static string Decode(ReadOnlySpan<byte> bytes, PlcDataType type, WordOrder order)
    {
        if (bytes.Length != Size(type)) throw new ArgumentException("数据长度与类型不一致。");
        byte[] b = Reorder(bytes.ToArray(), order); var ci = CultureInfo.InvariantCulture;
        return type switch { PlcDataType.Bool => b[0] == 0 ? "false" : "true", PlcDataType.Int16 => BinaryPrimitives.ReadInt16BigEndian(b).ToString(ci), PlcDataType.UInt16 => BinaryPrimitives.ReadUInt16BigEndian(b).ToString(ci), PlcDataType.Int32 => BinaryPrimitives.ReadInt32BigEndian(b).ToString(ci), PlcDataType.UInt32 => BinaryPrimitives.ReadUInt32BigEndian(b).ToString(ci), _ => BinaryPrimitives.ReadSingleBigEndian(b).ToString("G9", ci) };
    }
}

public sealed record PlcRequest(PlcAddress Address, PlcDataType Type, int Count, byte[]? Data = null)
{
    public int ByteCount => checked(Count * PlcValues.Size(Type));
    public int WireCount => Address.IsBit ? Count : Address.Protocol == PlcProtocol.SiemensS7 ? ByteCount : ByteCount / 2;
    public void Validate()
    {
        if (Count is < 1 or > 100 || ByteCount > 200) throw new ArgumentException("单次支持 1–100 个值，最多 200 字节；32 位类型最多 50 个。");
        if (Address.IsBit != (Type == PlcDataType.Bool)) throw new ArgumentException("地址类型不匹配。");
        if (Address.Protocol == PlcProtocol.SiemensS7 && Address.IsBit && Count != 1) throw new ArgumentException("S7 Bool 每次读取/写入一个位，数量请设为 1。");
        PlcMemory.ValidateRange(Address, ByteCount);
        if (Data is not null && (Data.Length != ByteCount || (Address.IsBit && Data.Any(x => x > 1)))) throw new ArgumentException("写入值数量或 Bool 值不正确。");
    }
}

/// <summary>Bounded, shared data areas. Bit access aliases the same bytes as word access.</summary>
public sealed class PlcMemory(PlcProtocol protocol)
{
    private readonly object _gate = new();
    private Dictionary<string, byte[]> _banks = [];
    public PlcProtocol Protocol { get; } = protocol;
    public static void ValidateRange(PlcAddress a, int count)
    {
        if (count < 1 || a.Index < 0 || a.Bit < 0) throw new ArgumentException("非法地址或长度。");
        long end = a.ByteOffset + (long)count;
        if (a.IsBit && !a.McBit)
        {
            int width = a.Protocol == PlcProtocol.SiemensS7 ? 8 : 16;
            if (a.Bit >= width) throw new ArgumentException("位号越界。");
            end = a.ByteOffset + ((a.Bit + (long)count - 1) / width + 1) * (width / 8);
        }
        if (end > a.BankSize) throw new ArgumentException("读写范围超出模拟数据区。");
    }
    public byte[] Read(PlcAddress a, int count)
    {
        Validate(a, count); byte[] result = new byte[count];
        lock (_gate)
        {
            if (!_banks.TryGetValue(a.Bank, out var bank)) return result;
            if (!a.IsBit || a.McBit) Array.Copy(bank, a.ByteOffset, result, 0, count);
            else for (int i = 0; i < count; i++) { var (offset, mask) = BitLocation(a, i); result[i] = (byte)((bank[offset] & mask) == 0 ? 0 : 1); }
        }
        return result;
    }
    public void Write(PlcAddress a, byte[] data)
    {
        Validate(a, data.Length);
        if (a.IsBit && data.Any(x => x > 1)) throw new ArgumentException("位值必须是 0 或 1。");
        lock (_gate)
        {
            if (!_banks.TryGetValue(a.Bank, out var bank))
            { if (_banks.Count >= 64) throw new IOException("模拟内存最多支持 64 个数据区/DB。"); _banks[a.Bank] = bank = new byte[a.BankSize]; }
            if (!a.IsBit || a.McBit) Array.Copy(data, 0, bank, a.ByteOffset, data.Length);
            else for (int i = 0; i < data.Length; i++) { var (offset, mask) = BitLocation(a, i); bank[offset] = (byte)(data[i] == 0 ? bank[offset] & ~mask : bank[offset] | mask); }
        }
    }
    private void Validate(PlcAddress a, int count) { if (a.Protocol != Protocol) throw new ArgumentException("数据区的协议不匹配。"); ValidateRange(a, count); }
    private static (int Offset, int Mask) BitLocation(PlcAddress a, int i)
    {
        int bit = a.Bit + i;
        if (a.Protocol == PlcProtocol.SiemensS7) return (a.Index + bit / 8, 1 << (bit % 8));
        return (a.Index * 2 + bit / 16 * 2 + (bit % 16 < 8 ? 1 : 0), 1 << (bit % 8)); // FINS words are big endian.
    }
    public string Snapshot() { lock (_gate) return JsonSerializer.Serialize(new MemorySnapshot(1, Protocol, _banks)); }
    public void Restore(string json)
    {
        if (json.Length > 12_000_000) throw new ArgumentException("快照超过 12 MB。");
        var snapshot = JsonSerializer.Deserialize<MemorySnapshot>(json) ?? throw new FormatException("无效快照。");
        if (snapshot.Version != 1 || snapshot.Protocol != Protocol || snapshot.Banks is null || snapshot.Banks.Count > 64) throw new FormatException("快照版本、协议或数据区数量不匹配。");
        foreach (var (key, bank) in snapshot.Banks)
        {
            string[] parts = key.Split('/');
            if (parts.Length != 3 || parts[0] != Protocol.ToString() || !byte.TryParse(parts[1], out byte area) || !ushort.TryParse(parts[2], out ushort block)) throw new FormatException("快照含非法数据区。");
            bool valid = Protocol switch { PlcProtocol.SiemensS7 => area == 0x84 ? block > 0 : area is 0x81 or 0x82 or 0x83 && block == 0, PlcProtocol.MitsubishiMc3E => block == 0 && area is 0xA8 or 0xB4 or 0xAF or 0x90 or 0x9C or 0x9D or 0x92 or 0xA0, _ => block == 0 && area is 0x82 or 0xB0 or 0xB1 or 0xB2 or 0xB3 };
            var a = new PlcAddress(Protocol, area, block, 0, 0, false, "");
            if (!valid || bank is null || bank.Length != a.BankSize || (a.McBit && bank.Any(x => x > 1))) throw new FormatException("快照数据区长度或内容无效。");
        }
        lock (_gate) _banks = snapshot.Banks;
    }
    private sealed record MemorySnapshot(int Version, PlcProtocol Protocol, Dictionary<string, byte[]> Banks);
}
