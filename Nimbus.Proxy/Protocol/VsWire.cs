using System.Text;

namespace Nimbus.Proxy;

// Shared Vintage Story wire-format primitives. One home for the two things every
// protocol class was hand-rolling separately:
//
//   * the VS TCP frame header: 4 big-endian bytes, bit 31 = zlib-compressed flag,
//     bits 30..0 = payload length;
//   * minimal protobuf writing (varints, tags, length-delimited fields), enough to
//     forge the handful of vanilla packets the proxy fabricates.
//
// Byte-for-byte identical to the previous per-class implementations; the wire tests
// in Nimbus.Proxy.Tests decode the produced frames with an independent reader.
internal static class VsWire
{
    public const int MaxFrameSize = 256 * 1024 * 1024; // VS uses a 128 MB MaxPacketSize

    // ---- frame header ----

    // Parses the 4-byte header. Returns false when fewer than 4 bytes are available.
    public static bool TryParseHeader(ReadOnlySpan<byte> bytes, out bool compressed, out int payloadLength)
    {
        compressed = false;
        payloadLength = 0;
        if (bytes.Length < 4) return false;
        uint header = (uint)((bytes[0] << 24) | (bytes[1] << 16) | (bytes[2] << 8) | bytes[3]);
        compressed = (header & 0x80000000u) != 0;
        payloadLength = (int)(header & 0x7FFFFFFFu);
        return true;
    }

    // Wraps a payload in an uncompressed VS frame (header + payload).
    public static byte[] WrapFrame(byte[] payload)
    {
        int len = payload.Length;
        var frame = new byte[4 + len];
        frame[0] = (byte)((len >> 24) & 0x7F);
        frame[1] = (byte)((len >> 16) & 0xFF);
        frame[2] = (byte)((len >> 8) & 0xFF);
        frame[3] = (byte)(len & 0xFF);
        Buffer.BlockCopy(payload, 0, frame, 4, len);
        return frame;
    }

    // ---- protobuf writing ----

    public static void WriteVarint(Stream s, ulong value)
    {
        while (value >= 0x80)
        {
            s.WriteByte((byte)(value | 0x80));
            value >>= 7;
        }
        s.WriteByte((byte)value);
    }

    public static void WriteTag(Stream s, int fieldNumber, int wireType)
        => WriteVarint(s, (ulong)((fieldNumber << 3) | wireType));

    public static void WriteVarintField(Stream s, int fieldNumber, ulong value)
    {
        WriteTag(s, fieldNumber, 0);
        WriteVarint(s, value);
    }

    public static void WriteBytesField(Stream s, int fieldNumber, ReadOnlySpan<byte> value)
    {
        WriteTag(s, fieldNumber, 2);
        WriteVarint(s, (ulong)value.Length);
        s.Write(value);
    }

    public static void WriteStringField(Stream s, int fieldNumber, string value)
        => WriteBytesField(s, fieldNumber, Encoding.UTF8.GetBytes(value));

    // Envelope helper for forged server packets: Packet_Server.Id (field 90) as a varint,
    // then the nested body under its own field number, wrapped in a frame.
    public static byte[] BuildServerPacketFrame(int packetId, int bodyField, byte[] body)
    {
        var env = new MemoryStream();
        WriteVarintField(env, 90, (ulong)packetId);
        WriteBytesField(env, bodyField, body);
        return WrapFrame(env.ToArray());
    }
}
