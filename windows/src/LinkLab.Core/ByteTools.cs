using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace LinkLab.Core;

public enum ChecksumKind { None, Crc16Modbus, Sum8, Xor8, Lrc, Crc32 }
public enum WordOrder { ABCD, BADC, CDAB, DCBA }

public static class ByteTools
{
    public static byte[] ParseHex(string text)
    {
        var clean = new StringBuilder();
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsWhiteSpace(c) || c is ',' or '-' or ':' or '_') continue;
            if (c == '0' && i + 1 < text.Length && text[i + 1] is 'x' or 'X') { i++; continue; }
            if (!Uri.IsHexDigit(c)) throw new FormatException($"HEX 中包含无效字符：{c}");
            clean.Append(c);
        }
        if (clean.Length % 2 != 0) throw new FormatException("HEX 必须由完整字节组成，例如 01 03 00 00。");
        return Convert.FromHexString(clean.ToString());
    }

    public static string Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexString(bytes).Chunk(2).Select(x => new string(x)).Aggregate(new StringBuilder(), (s, x) => s.Append(s.Length == 0 ? "" : " ").Append(x)).ToString();
    public static string Printable(ReadOnlySpan<byte> bytes) => new(Encoding.UTF8.GetString(bytes).Select(c => char.IsControl(c) && c is not '\r' and not '\n' and not '\t' ? '·' : c).ToArray());

    public static ushort Crc16(ReadOnlySpan<byte> bytes)
    {
        ushort crc = 0xFFFF;
        foreach (var b in bytes)
        {
            crc ^= b;
            for (int n = 0; n < 8; n++) crc = (ushort)((crc & 1) != 0 ? (crc >> 1) ^ 0xA001 : crc >> 1);
        }
        return crc;
    }
    public static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        uint crc = 0xFFFFFFFF;
        foreach (var b in bytes)
        {
            crc ^= b;
            for (int n = 0; n < 8; n++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
        }
        return ~crc;
    }
    public static byte[] AppendChecksum(byte[] data, ChecksumKind kind)
    {
        int sum = 0; byte xor = 0;
        foreach (var b in data) { sum += b; xor ^= b; }
        byte[] check = kind switch
        {
            ChecksumKind.None => [],
            ChecksumKind.Crc16Modbus => [(byte)Crc16(data), (byte)(Crc16(data) >> 8)],
            ChecksumKind.Sum8 => [unchecked((byte)sum)],
            ChecksumKind.Xor8 => [xor],
            ChecksumKind.Lrc => [unchecked((byte)-sum)],
            ChecksumKind.Crc32 => BitConverter.GetBytes(Crc32(data)),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        return [.. data, .. check];
    }
    public static bool ValidRtu(ReadOnlySpan<byte> data) => data.Length >= 4 && Crc16(data[..^2]) == BinaryPrimitives.ReadUInt16LittleEndian(data[^2..]);

    public static int Number(string value)
    {
        value = value.Trim();
        return value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? int.Parse(value[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture)
            : int.Parse(value, CultureInfo.InvariantCulture);
    }

    public static string Decode(ushort[] registers, int offset, string type, WordOrder order)
    {
        if (offset >= registers.Length) return "—";
        ushort val = registers[offset];
        if (type == "UInt16") return val.ToString(CultureInfo.InvariantCulture);
        if (type == "Int16") return unchecked((short)val).ToString(CultureInfo.InvariantCulture);
        if (type == "HEX") return $"0x{val:X4}";
        if (offset + 1 >= registers.Length) return "需 2 个寄存器";
        byte[] bytes = [(byte)(val >> 8), (byte)val, (byte)(registers[offset + 1] >> 8), (byte)registers[offset + 1]];
        bytes = order switch { WordOrder.BADC => [bytes[1], bytes[0], bytes[3], bytes[2]], WordOrder.CDAB => [bytes[2], bytes[3], bytes[0], bytes[1]], WordOrder.DCBA => [bytes[3], bytes[2], bytes[1], bytes[0]], _ => bytes };
        uint bits = BinaryPrimitives.ReadUInt32BigEndian(bytes);
        return type switch { "UInt32" => bits.ToString(CultureInfo.InvariantCulture), "Int32" => unchecked((int)bits).ToString(CultureInfo.InvariantCulture), "Float32" => BitConverter.UInt32BitsToSingle(bits).ToString("G9", CultureInfo.InvariantCulture), _ => throw new ArgumentException("未知数值类型") };
    }
}
