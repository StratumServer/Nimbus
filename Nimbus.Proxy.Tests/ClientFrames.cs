namespace Nimbus.Proxy.Tests;

/// <summary>
/// Client-to-server frames a real Vintage Story client sends during the handshake, built with the
/// independent wire writer in ProtoWire so the tests never lean on the proxy's own encoders.
/// </summary>
internal static class ClientFrames
{
    /// <summary>
    /// Identification, shaped the way IdentificationParser reads it: the VS frame header, then a
    /// Packet_Client envelope whose field 2 wraps the identification body (2 = name, 6 = uid).
    /// </summary>
    public static byte[] Identification(string uid, string name)
    {
        var ident = new MemoryStream();
        ProtoWire.WriteString(ident, 2, name);
        ProtoWire.WriteString(ident, 6, uid);

        var envelope = new MemoryStream();
        ProtoWire.WriteBytes(envelope, 2, ident.ToArray());
        return ProtoWire.Frame(envelope.ToArray());
    }

    /// <summary>
    /// LoginTokenQuery: the actual first frame of a stock client. Packet_Client field 33 with an
    /// empty body, which is tag 266 in PacketDispatch.ClientTags. No identity anywhere in it.
    /// </summary>
    public static byte[] LoginTokenQuery()
    {
        var envelope = new MemoryStream();
        ProtoWire.WriteBytes(envelope, 33, Array.Empty<byte>());
        return ProtoWire.Frame(envelope.ToArray());
    }
}
