using System.Text;

namespace Nimbus.Proxy;

/// <summary>
/// Builds a vanilla VS <c>Packet_Server { Id = 29, Redirect = { Host, Name } }</c> frame at the
/// raw wire level, ready to be written to a client socket (4-byte BE length header included,
/// uncompressed). The client treats this packet as "drop this connection and reconnect to Host".
///
/// Wire layout (matches Packet_ServerSerializer + Packet_ServerRedirectSerializer):
///   envelope:  D0 05 1D                 field 90 (Id) varint 29   [IdFieldID = 90]
///              EA 01 &lt;len&gt; &lt;body&gt;      field 29 (Redirect) length-delimited
///   body:      0A &lt;n&gt; &lt;name-utf8&gt;       field 1 (Name)
///              12 &lt;n&gt; &lt;host-utf8&gt;       field 2 (Host)
///
/// The 4-byte header is big-endian payload length with bit 31 = compressed flag (we always
/// emit uncompressed, so bit 31 = 0).
/// </summary>
internal static class RedirectBuilder
{
    /// <summary>
    /// Build a framed redirect packet. <paramref name="host"/> follows vanilla
    /// <see cref="Vintagestory.Server.ServerMain.SendServerRedirect"/> conventions:
    /// "host" or "host:port" (port omitted when 42420).
    /// </summary>
    public static byte[] BuildRedirectFrame(string host, string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(host);
        name ??= "";

        // Body
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
        // NOTE: Packet_Server.Id has FieldID = 90 (NOT 1; field 1 is Identification).
        var env = new MemoryStream();
        WriteTag(env, fieldNumber: 90, wireType: 0); // Id
        WriteVarint(env, 29u);
        WriteTag(env, fieldNumber: 29, wireType: 2); // Redirect
        WriteVarint(env, (uint)bodyBytes.Length);
        env.Write(bodyBytes, 0, bodyBytes.Length);
        byte[] envBytes = env.ToArray();

        // 4-byte BE length header (uncompressed: bit 31 = 0)
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
