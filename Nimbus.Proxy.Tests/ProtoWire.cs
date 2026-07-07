using System.Text;

namespace Nimbus.Proxy.Tests;

/// <summary>
/// Minimal protobuf wire-format writer/reader, REIMPLEMENTED for the tests on purpose:
/// the builders under test hand-roll their encoding, so decoding their output with an
/// independent implementation (instead of calling back into the same helpers) is what
/// actually proves the bytes are valid protobuf a Vintage Story client can parse.
/// </summary>
internal static class ProtoWire
{
    // ---- writing (to build test inputs) ----

    public static void WriteVarint(MemoryStream s, ulong value)
    {
        while (value >= 0x80)
        {
            s.WriteByte((byte)(value | 0x80));
            value >>= 7;
        }
        s.WriteByte((byte)value);
    }

    public static void WriteTag(MemoryStream s, int field, int wireType)
        => WriteVarint(s, (ulong)((field << 3) | wireType));

    public static void WriteString(MemoryStream s, int field, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        WriteTag(s, field, 2);
        WriteVarint(s, (ulong)bytes.Length);
        s.Write(bytes);
    }

    public static void WriteBytes(MemoryStream s, int field, byte[] value)
    {
        WriteTag(s, field, 2);
        WriteVarint(s, (ulong)value.Length);
        s.Write(value);
    }

    /// <summary>Wraps a payload in the VS TCP frame header (4 BE bytes, bit 31 = compressed).</summary>
    public static byte[] Frame(byte[] payload, bool compressed = false)
    {
        var frame = new byte[4 + payload.Length];
        uint header = (uint)payload.Length;
        if (compressed) header |= 0x80000000u;
        frame[0] = (byte)(header >> 24);
        frame[1] = (byte)(header >> 16);
        frame[2] = (byte)(header >> 8);
        frame[3] = (byte)header;
        payload.CopyTo(frame, 4);
        return frame;
    }

    // ---- reading (to verify built frames) ----

    public sealed record Field(int Number, int WireType, ulong Varint, byte[] Bytes);

    public static (bool Compressed, int PayloadLength, byte[] Payload) ParseFrame(byte[] frame)
    {
        Assert(frame.Length >= 4, "frame shorter than its header");
        uint header = (uint)((frame[0] << 24) | (frame[1] << 16) | (frame[2] << 8) | frame[3]);
        bool compressed = (header & 0x80000000u) != 0;
        int len = (int)(header & 0x7FFFFFFFu);
        Assert(frame.Length == 4 + len, $"frame length {frame.Length} != 4 + declared {len}");
        return (compressed, len, frame[4..]);
    }

    public static List<Field> ReadFields(byte[] payload)
    {
        var fields = new List<Field>();
        int pos = 0;
        while (pos < payload.Length)
        {
            ulong key = ReadVarint(payload, ref pos);
            int number = (int)(key >> 3);
            int wireType = (int)(key & 0x7);
            switch (wireType)
            {
                case 0:
                    fields.Add(new Field(number, 0, ReadVarint(payload, ref pos), []));
                    break;
                case 2:
                    int len = (int)ReadVarint(payload, ref pos);
                    Assert(pos + len <= payload.Length, "length-delimited field overruns payload");
                    fields.Add(new Field(number, 2, 0, payload[pos..(pos + len)]));
                    pos += len;
                    break;
                default:
                    throw new InvalidOperationException($"unexpected wire type {wireType} in built frame");
            }
        }
        return fields;
    }

    public static Field Single(List<Field> fields, int number)
        => fields.Single(f => f.Number == number);

    public static string Utf8(Field f) => Encoding.UTF8.GetString(f.Bytes);

    private static ulong ReadVarint(byte[] buf, ref int pos)
    {
        ulong value = 0;
        int shift = 0;
        while (pos < buf.Length)
        {
            byte b = buf[pos++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return value;
            shift += 7;
        }
        throw new InvalidOperationException("truncated varint");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
