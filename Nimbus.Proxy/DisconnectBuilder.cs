using System.Text;

namespace Nimbus.Proxy;

// Builds a vanilla VS Packet_Server { Id = 9, DisconnectPlayer = { DisconnectReason } } frame
// at the wire level. The client's HandleDisconnectPlayer routes through a full Dispose() before
// showing the disconnect screen, so this is the cleanest server-side teardown that works on a
// vanilla (unmodded) client.
//
// Wire layout (matches Packet_ServerSerializer + Packet_ServerDisconnectPlayerSerializer):
//   envelope:  D0 05 09                 field 90 (Id) varint 9
//              42 <len> <body>          field 8 (DisconnectPlayer) length-delimited
//   body:      0A <n> <reason-utf8>     field 1 (DisconnectReason)
//
// The 4-byte frame header is big-endian length with bit 31 = compressed. We always emit
// uncompressed, so bit 31 is 0.
internal static class DisconnectBuilder
{
    // Build a framed Disconnect packet. Reason is shown verbatim on the client.
    public static byte[] BuildDisconnectFrame(string reason)
    {
        reason ??= "";

        // Body: DisconnectReason field 1, wire 2, length-delimited UTF-8.
        byte[] reasonBytes = Encoding.UTF8.GetBytes(reason);
        var body = new MemoryStream();
        WriteTag(body, fieldNumber: 1, wireType: 2);
        WriteVarint(body, (uint)reasonBytes.Length);
        body.Write(reasonBytes, 0, reasonBytes.Length);
        byte[] bodyBytes = body.ToArray();

        // Envelope: Id varint (field 90), DisconnectPlayer length-delimited (field 8).
        var env = new MemoryStream();
        WriteTag(env, fieldNumber: 90, wireType: 0); // Id
        WriteVarint(env, 9u);
        WriteTag(env, fieldNumber: 8, wireType: 2);  // DisconnectPlayer
        WriteVarint(env, (uint)bodyBytes.Length);
        env.Write(bodyBytes, 0, bodyBytes.Length);
        byte[] envBytes = env.ToArray();

        // 4-byte BE length header (uncompressed: bit 31 = 0).
        int payloadLen = envBytes.Length;
        var frame = new byte[4 + payloadLen];
        frame[0] = (byte)((payloadLen >> 24) & 0x7F);
        frame[1] = (byte)((payloadLen >> 16) & 0xFF);
        frame[2] = (byte)((payloadLen >> 8) & 0xFF);
        frame[3] = (byte)(payloadLen & 0xFF);
        Buffer.BlockCopy(envBytes, 0, frame, 4, payloadLen);
        return frame;
    }

    private static void WriteTag(Stream s, int fieldNumber, int wireType)
        => WriteVarint(s, (uint)((fieldNumber << 3) | wireType));

    private static void WriteVarint(Stream s, uint value)
    {
        while (value >= 0x80)
        {
            s.WriteByte((byte)(value | 0x80));
            value >>= 7;
        }
        s.WriteByte((byte)value);
    }
}
