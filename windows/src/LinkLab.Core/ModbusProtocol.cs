using System.Buffers.Binary;

namespace LinkLab.Core;

public sealed record ModbusRequest(byte Unit, byte Function, ushort Address, ushort Quantity, ushort[] Values)
{
    public byte[] Pdu()
    {
        int maximum = Function switch { 1 or 2 => 2000, 3 or 4 => 125, 5 or 6 => 1, 15 => 1968, 16 => 123, _ => throw new ArgumentException("不支持的功能码") };
        if (Quantity < 1 || Quantity > maximum || (int)Address + Quantity > 65536) throw new ArgumentException($"数量应为 1–{maximum}，且地址不可超出 65535。");
        bool write = Function is 5 or 6 or 15 or 16;
        if (write && Values.Length != Quantity) throw new ArgumentException("写入数值个数必须等于数量。");
        if (Function is 5 or 15 && Values.Any(v => v > 1)) throw new ArgumentException("线圈值只能是 0 或 1。");
        var pdu = new List<byte> { Function, (byte)(Address >> 8), (byte)Address };
        ushort value = Function switch { 5 => (ushort)(Values[0] == 0 ? 0 : 0xFF00), 6 => Values[0], _ => Quantity };
        pdu.Add((byte)(value >> 8)); pdu.Add((byte)value);
        if (Function == 15)
        {
            byte[] packed = new byte[(Quantity + 7) / 8];
            for (int i = 0; i < Quantity; i++) if (Values[i] != 0) packed[i / 8] |= (byte)(1 << (i % 8));
            pdu.Add((byte)packed.Length); pdu.AddRange(packed);
        }
        if (Function == 16)
        {
            pdu.Add((byte)(Quantity * 2));
            foreach (var v in Values) { pdu.Add((byte)(v >> 8)); pdu.Add((byte)v); }
        }
        return pdu.ToArray();
    }
    public byte[] Rtu() => ByteTools.AppendChecksum([Unit, .. Pdu()], ChecksumKind.Crc16Modbus);
    public byte[] Tcp(ushort transaction = 1) => ModbusProtocol.TcpFrame(transaction, Unit, Pdu());
}

public sealed class ModbusException(byte code) : IOException($"Modbus 异常 0x{code:X2}：{ModbusProtocol.ExceptionName(code)}") { public byte Code { get; } = code; }

public static class ModbusProtocol
{
    public static readonly string[] Functions = ["01 · 读线圈", "02 · 读离散输入", "03 · 读保持寄存器", "04 · 读输入寄存器", "05 · 写单线圈", "06 · 写单寄存器", "0F · 写多线圈", "10 · 写多寄存器"];
    public static readonly byte[] FunctionCodes = [1, 2, 3, 4, 5, 6, 15, 16];
    public static string ExceptionName(byte code) => code switch { 1 => "非法功能", 2 => "非法数据地址", 3 => "非法数据值", 4 => "设备故障", 6 => "设备忙", 10 => "网关路径不可用", 11 => "网关目标未响应", _ => "未知异常" };
    public static ushort Address(int entered, bool reference, byte function)
    {
        int start = function switch { 1 or 5 or 15 => 1, 2 => 10001, 4 => 30001, _ => 40001 };
        int raw = reference ? entered - start : entered;
        if (raw is < 0 or > 65535) throw new ArgumentException(reference ? $"当前功能的引用地址从 {start} 开始。" : "协议地址必须在 0–65535 之间。");
        return (ushort)raw;
    }
    public static byte[] TcpFrame(ushort transaction, byte unit, ReadOnlySpan<byte> pdu)
    {
        if (pdu.Length is < 1 or > 253) throw new ArgumentException("Modbus PDU 长度错误。");
        int len = pdu.Length + 1;
        return [(byte)(transaction >> 8), (byte)transaction, 0, 0, (byte)(len >> 8), (byte)len, unit, .. pdu];
    }
    public static ushort[] ParseResponse(ModbusRequest request, ReadOnlySpan<byte> pdu)
    {
        if (pdu.Length == 2 && pdu[0] == (request.Function | 0x80)) throw new ModbusException(pdu[1]);
        if (pdu.Length < 2 || pdu[0] != request.Function) throw new IOException("响应功能码或长度不匹配。");
        if (request.Function is >= 1 and <= 4)
        {
            int len = request.Function <= 2 ? (request.Quantity + 7) / 8 : request.Quantity * 2;
            if (pdu[1] != len || pdu.Length != len + 2) throw new IOException("响应数据长度与请求不匹配。");
            var result = new ushort[request.Quantity];
            for (int i = 0; i < result.Length; i++) result[i] = request.Function <= 2 ? (ushort)((pdu[2 + i / 8] >> (i % 8)) & 1) : BinaryPrimitives.ReadUInt16BigEndian(pdu.Slice(2 + i * 2, 2));
            return result;
        }
        if (pdu.Length != 5 || !pdu.SequenceEqual(request.Pdu().AsSpan(0, 5))) throw new IOException("写入确认与请求不匹配。");
        return request.Values;
    }
    public static string Describe(byte[] frame, bool tcp)
    {
        int offset = tcp ? 7 : 1;
        if (frame.Length < offset + 1 + (tcp ? 0 : 2)) throw new FormatException("报文太短。");
        if (tcp && (frame[2] != 0 || frame[3] != 0 || BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(4, 2)) != frame.Length - 6)) throw new FormatException("MBAP 协议标识或长度错误。");
        byte unit = frame[tcp ? 6 : 0], fc = frame[offset];
        string prefix = tcp ? $"事务 ID：{BinaryPrimitives.ReadUInt16BigEndian(frame)}  |  MBAP 长度：{frame.Length - 6}" : $"CRC16：{(ByteTools.ValidRtu(frame) ? "通过" : "错误")}";
        var pdu = frame.AsSpan(offset, frame.Length - offset - (tcp ? 0 : 2));
        string info = (fc & 0x80) != 0 && pdu.Length == 2 ? $"异常：{ExceptionName(pdu[1])}" : $"PDU（{pdu.Length} 字节）：{ByteTools.Hex(pdu)}";
        return $"{prefix}\n站号：{unit}  |  功能码：0x{fc:X2}\n{info}\n布局：{(tcp ? "事务 ID [0..1] / 协议 [2..3] / 长度 [4..5] / 站号 [6] / PDU [7..]" : "站号 [0] / PDU [1..] / CRC [末尾 2 字节，低字节在前]")}";
    }
}
