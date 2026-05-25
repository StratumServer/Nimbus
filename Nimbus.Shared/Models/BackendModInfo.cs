namespace Nimbus.Shared.Models;

// Required client mod entry reported in a backend heartbeat. Mirrors vanilla ModPacket.
public sealed class BackendModInfo
{
    public string Id { get; set; } = "";
    public string Version { get; set; } = "";
}
