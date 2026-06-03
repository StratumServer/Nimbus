using System.Text;

namespace Nimbus.Proxy;

internal static class RedirectBuilder
{
    public static byte[] BuildRedirectFrame(string host, string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(host);
        name ??= "";

        byte[] nameBytes = Encoding.UTF8.GetBytes(name);
        byte[] hostBytes = Encoding.UTF8.GetBytes(host);

        var body = new MemoryStream();
        WriteTag(body, fieldNumber: 1, wireType: 2); // Name
        WriteVarint(body, (uint)nameBytes.Length);
        body.Write(nameBytes, 0, nameBytes.Length);
        WriteTag(body, fieldNumber: 2, wireType: 2); // Host
        WriteVarint(body, (uint)hostBytes.Length);
        body.Write(hostBytes, 0, hostBytes.Length);
        byte[] bodyBytes = body.ToArray();

        // Envelope: Id varint (field 90), Redirect length-delimited (field 29).
        // Packet_Server.Id has FieldID = 90. Field 1 is Identification.
        var env = new MemoryStream();
        WriteTag(env, fieldNumber: 90, wireType: 0); // Id
        WriteVarint(env, 29u);
        WriteTag(env, fieldNumber: 29, wireType: 2); // Redirect
        WriteVarint(env, (uint)bodyBytes.Length);
        env.Write(bodyBytes, 0, bodyBytes.Length);
        byte[] envBytes = env.ToArray();

        // VS frames start with a big-endian payload length. Bit 31 is the compressed flag.
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
