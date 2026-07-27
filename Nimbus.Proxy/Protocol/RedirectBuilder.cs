namespace Nimbus.Proxy;

internal static class RedirectBuilder
{
    public static byte[] BuildRedirectFrame(string host, string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(host);
        name ??= "";

        var body = new MemoryStream();
        VsWire.WriteStringField(body, 1, name);
        VsWire.WriteStringField(body, 2, host);

        // Packet_Server envelope: Id (field 90) = 29, Redirect body under field 29.
        return VsWire.BuildServerPacketFrame(packetId: 29, bodyField: 29, body.ToArray());
    }
}
