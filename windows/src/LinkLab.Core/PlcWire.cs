using System.Buffers.Binary;
using IoTClient.Clients.PLC;
using IoTClient.Clients.PLC.Models;
using IoTClient.Common.Enums;
using IoTClient.Core.Models;
using IoTClient.Enums;
using IoTClient.Models;

namespace LinkLab.Core;

/// <summary>Uses MIT IoTClient command builders; framing, cancellation and reply validation belong to LinkLab.</summary>
public static class PlcWire
{
    private sealed class S7Builder() : SiemensClient(SiemensVersion.S7_1200, "127.0.0.1", 102)
    {
        public byte[] Build(PlcRequest r)
        {
            var a = new SiemensAddress { TypeCode = r.Address.Area, DbBlock = r.Address.Block, BeginAddress = r.Address.Index * 8 + r.Address.Bit, ReadWriteLength = (ushort)r.WireCount, ReadWriteBit = r.Address.IsBit };
            return r.Data is null ? GetReadCommand(a) : GetWriteCommand(new SiemensWriteAddress(a) { WriteData = r.Data });
        }
    }
    private sealed class McBuilder() : MitsubishiClient(MitsubishiVersion.Qna_3E, "127.0.0.1", 6000)
    {
        public byte[] Build(PlcRequest r)
        {
            if (r.Data is null) return GetReadCommand_Qna_3E(r.Address.Index, [r.Address.Area], (ushort)r.ByteCount, r.Address.IsBit);
            byte[] data = r.Address.IsBit ? PackBits(r.Data) : r.Data;
            var frame = GetWriteCommand_Qna_3E(r.Address.Index, [r.Address.Area], data.Reverse().ToArray(), r.Address.IsBit);
            // The library builder assumes one bit on writes; support a complete packed bit array.
            PutLe(frame, 19, r.WireCount); return frame;
        }
    }
    private sealed class FinsBuilder() : OmronFinsClient("127.0.0.1")
    {
        public byte[] Build(PlcRequest r)
        {
            var a = new OmronFinsAddress { IsBit = r.Address.IsBit, WordCode = r.Address.Area, BitCode = (byte)(r.Address.Area - 0x80), BitAddress = [(byte)(r.Address.Index >> 8), (byte)r.Address.Index, (byte)r.Address.Bit] };
            return r.Data is null ? GetReadCommand(a, (ushort)r.ByteCount) : GetWriteCommand(a, r.Data);
        }
    }
    public static byte[] Request(PlcRequest r, PlcOptions options, ushort sequence, byte clientNode = 1, byte serverNode = 1)
    {
        r.Validate(); byte[] f;
        switch (options.Protocol)
        {
            case PlcProtocol.SiemensS7: f = new S7Builder().Build(r); PutBe(f, 11, sequence); return f;
            case PlcProtocol.MitsubishiMc3E: f = new McBuilder().Build(r); f[2] = options.Network; f[3] = options.Station; PutLe(f, 4, options.IoNumber); f[6] = options.Unit; return f;
            default: f = new FinsBuilder().Build(r); f[19] = options.Network; f[20] = serverNode; f[21] = options.Unit; f[22] = options.SourceNetwork; f[23] = clientNode; f[25] = (byte)sequence; return f;
        }
    }
    public static byte[] ParseResponse(PlcRequest r, byte[] request, byte[] f)
    {
        if (r.Address.Protocol == PlcProtocol.SiemensS7)
        {
            if (f.Length < 19 || f[5] != 0xF0 || f[6] != 0x80 || f[7] != 0x32 || f[8] != 3 || Be(f, 11) != Be(request, 11) || 19 + Be(f, 13) + Be(f, 15) != f.Length) throw new IOException("S7 响应头、序列号或长度不匹配。");
            if (Be(f, 17) != 0) throw new PlcException($"S7 错误 0x{Be(f, 17):X4}");
            if (f.Length < 22 || Be(f, 13) != 2 || f[19] != request[17] || f[20] != 1) throw new IOException("S7 响应功能或项目数量不匹配。");
            if (f[21] != 0xFF) throw new PlcException($"S7 数据区错误 0x{f[21]:X2}（05 地址越界，0A 数据区不存在）");
            if (r.Data is not null) { if (f.Length != 22) throw new IOException("S7 写响应长度错误。"); return r.Data.ToArray(); }
            if (f.Length != 25 + r.ByteCount || f[22] != (r.Address.IsBit ? 3 : 4) || Be(f, 23) != (r.Address.IsBit ? r.Count : r.ByteCount * 8)) throw new IOException("S7 返回的数据类型或长度不匹配。");
            return f[25..];
        }
        if (r.Address.Protocol == PlcProtocol.MitsubishiMc3E)
        {
            if (f.Length < 11 || f[0] != 0xD0 || f[1] != 0 || !f.AsSpan(2, 5).SequenceEqual(request.AsSpan(2, 5)) || Le(f, 7) != f.Length - 9) throw new IOException("MC 响应头、路由或长度不匹配。");
            if (Le(f, 9) != 0) throw new PlcException($"MC 结束码 0x{Le(f, 9):X4}");
            int expected = r.Data is not null ? 0 : r.Address.IsBit ? (r.Count + 1) / 2 : r.ByteCount;
            if (f.Length != 11 + expected) throw new IOException("MC 返回的数据长度不匹配。");
            return r.Data?.ToArray() ?? (r.Address.IsBit ? UnpackBits(f[11..], r.Count) : f[11..]);
        }
        if (f.Length < 30 || U32(f, 8) != 2 || U32(f, 12) != 0 || f[16] != 0xC0 || f[25] != request[25] || !f.AsSpan(26, 2).SequenceEqual(request.AsSpan(26, 2)) || !f.AsSpan(19, 3).SequenceEqual(request.AsSpan(22, 3)) || !f.AsSpan(22, 3).SequenceEqual(request.AsSpan(19, 3))) throw new IOException("FINS 响应命令、节点或 SID 不匹配。");
        if (Be(f, 28) != 0) throw new PlcException($"FINS 结束码 0x{Be(f, 28):X4}");
        if (f.Length != 30 + (r.Data is null ? r.ByteCount : 0)) throw new IOException("FINS 返回的数据长度不匹配。");
        if (r.Address.IsBit && f.AsSpan(30).ContainsAnyExcept((byte)0, (byte)1)) throw new IOException("FINS 返回了非法位值。");
        return r.Data?.ToArray() ?? f[30..];
    }
    public static async Task<byte[]> ReadFrame(Stream stream, PlcProtocol protocol, CancellationToken ct)
    {
        int header = protocol switch { PlcProtocol.SiemensS7 => 4, PlcProtocol.MitsubishiMc3E => 9, _ => 8 };
        byte[] h = new byte[header]; await stream.ReadExactlyAsync(h, ct);
        long rest = protocol switch { PlcProtocol.SiemensS7 => Be(h, 2) - 4, PlcProtocol.MitsubishiMc3E => Le(h, 7), _ => U32(h, 4) };
        if (rest < 2 || rest > 8192 || (protocol == PlcProtocol.SiemensS7 && (h[0] != 3 || h[1] != 0)) || (protocol == PlcProtocol.OmronFinsTcp && !h.AsSpan(0, 4).SequenceEqual("FINS"u8))) throw new IOException("非法 PLC 报文头或长度。");
        var f = new byte[header + (int)rest]; h.CopyTo(f, 0); await stream.ReadExactlyAsync(f.AsMemory(header), ct); return f;
    }
    public static byte[] Cotp(PlcOptions o)
    {
        byte[] f = [3, 0, 0, 22, 17, 0xE0, 0, 0, 0, 1, 0, 0xC1, 2, 1, 0, 0xC2, 2, 3, 1, 0xC0, 1, 10];
        PutBe(f, 13, o.LocalTsap); PutBe(f, 17, o.RemoteTsap != 0 ? o.RemoteTsap : 0x0300 | o.Rack * 32 + o.Slot); return f;
    }
    public static byte[] S7Job(ushort sequence, byte[] parameter) { byte[] f = new byte[17 + parameter.Length]; f[0] = 3; PutBe(f, 2, f.Length); f[4] = 2; f[5] = 0xF0; f[6] = 0x80; f[7] = 0x32; f[8] = 1; PutBe(f, 11, sequence); PutBe(f, 13, parameter.Length); parameter.CopyTo(f, 17); return f; }
    public static byte[] S7Ack(byte[] request, byte[] parameter, byte[] data, ushort error = 0)
    {
        byte[] f = new byte[19 + parameter.Length + data.Length]; f[0] = 3; PutBe(f, 2, f.Length); f[4] = 2; f[5] = 0xF0; f[6] = 0x80; f[7] = 0x32; f[8] = 3; PutBe(f, 11, Be(request, 11)); PutBe(f, 13, parameter.Length); PutBe(f, 15, data.Length); PutBe(f, 17, error); parameter.CopyTo(f, 19); data.CopyTo(f, 19 + parameter.Length); return f;
    }
    public static byte[] Fins(uint command, byte[] body, uint error = 0) { byte[] f = new byte[16 + body.Length]; "FINS"u8.CopyTo(f); Put32(f, 4, (uint)(f.Length - 8)); Put32(f, 8, command); Put32(f, 12, error); body.CopyTo(f, 16); return f; }
    public static byte[] PackBits(byte[] values) { byte[] b = new byte[(values.Length + 1) / 2]; for (int i = 0; i < values.Length; i++) b[i / 2] |= (byte)(values[i] << (i % 2 == 0 ? 4 : 0)); return b; }
    public static byte[] UnpackBits(byte[] data, int count) { byte[] b = new byte[count]; for (int i = 0; i < count; i++) { b[i] = (byte)((data[i / 2] >> (i % 2 == 0 ? 4 : 0)) & 15); if (b[i] > 1) throw new ArgumentException("MC 位编码无效。"); } return b; }
    public static ushort Be(byte[] b, int i) => BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(i));
    public static ushort Le(byte[] b, int i) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(i));
    public static uint U32(byte[] b, int i) => BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(i));
    public static void PutBe(byte[] b, int i, int n) => BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(i), checked((ushort)n));
    public static void PutLe(byte[] b, int i, int n) => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(i), checked((ushort)n));
    public static void Put32(byte[] b, int i, uint n) => BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(i), n);
}
public sealed class PlcException(string message) : IOException(message);
