using System.Text;

namespace Nimbus.Proxy;

// This likely needs removed or redone
internal static class DisconnectBuilder
{
    public static byte[] BuildDisconnectFrame(string reason)
    {
        reason ??= "";

        byte[] reasonBytes = Encoding.UTF8.GetBytes(reason);
        var body = new MemoryStream();
        WriteTag(body, fieldNumber: 1, wireType: 2);
        WriteVarint(body, (uint)reasonBytes.Length);
        body.Write(reasonBytes, 0, reasonBytes.Length);
        byte[] bodyBytes = body.ToArray();

        var env = new MemoryStream();
        WriteTag(env, fieldNumber: 90, wireType: 0); // Id
        WriteVarint(env, 9u);
        WriteTag(env, fieldNumber: 8, wireType: 2);  // DisconnectPlayer
        WriteVarint(env, (uint)bodyBytes.Length);
        env.Write(bodyBytes, 0, bodyBytes.Length);
        byte[] envBytes = env.ToArray();

        // VS frame header. Bit 31 is the compressed flag.
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
