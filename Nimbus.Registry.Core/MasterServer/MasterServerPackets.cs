namespace Nimbus.Registry.MasterServer;

// Mirrors Vintagestory.Server.RegisterRequestPacket on the wire. Lowercase field names
// and the capital "Mods" match what the master server expects.
internal sealed class RegisterRequestPacket
{
    public ushort port { get; set; }
    public string name { get; set; } = "";
    public string icon { get; set; } = "";
    public PlaystylePacket playstyle { get; set; } = new();
    public ushort maxPlayers { get; set; }
    public string gameVersion { get; set; } = "";
    public bool hasPassword { get; set; }
    public ModPacket[] Mods { get; set; } = Array.Empty<ModPacket>();
    public string serverUrl { get; set; } = "";
    public string gameDescription { get; set; } = "";
    public bool whitelisted { get; set; }
    public string vhIdentifier { get; set; } = "";
}

internal sealed class HeartbeatPacket
{
    public string token { get; set; } = "";
    public int players { get; set; }
}

internal sealed class UnregisterPacket
{
    public string token { get; set; } = "";
}

internal sealed class PlaystylePacket
{
    public string id { get; set; } = "";
    public string langCode { get; set; } = "";
}

internal sealed class ModPacket
{
    public string id { get; set; } = "";
    public string version { get; set; } = "";
}

internal sealed class ResponsePacket
{
    public string status { get; set; } = "";
    public string data { get; set; } = "";
}
