namespace Nimbus.Proxy;

internal static class DisconnectBuilder
{
    public static byte[] BuildDisconnectFrame(string reason)
    {
        reason ??= "";

        var body = new MemoryStream();
        VsWire.WriteStringField(body, 1, reason);

        // Packet_Server envelope: Id (field 90) = 9, DisconnectPlayer body under field 8.
        return VsWire.BuildServerPacketFrame(packetId: 9, bodyField: 8, body.ToArray());
    }
}
