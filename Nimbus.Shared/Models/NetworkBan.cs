namespace Nimbus.Shared.Models;

// A ban held by the registry so it applies across the whole network instead of one savegame.
// Keyed on PlayerUid: names change, uids do not.
public sealed class NetworkBan
{
    public string PlayerUid { get; set; } = "";

    // Last known name, for readable ban lists. Never used for matching.
    public string PlayerName { get; set; } = "";

    // Empty means network-wide: the proxy refuses the connection outright. Otherwise the ban
    // is scoped to that one backend, which stays reachable for everyone else.
    public string ServerId { get; set; } = "";

    public string Reason { get; set; } = "";
    public string BannedBy { get; set; } = "";
    public long CreatedAtUnix { get; set; }

    // 0 means permanent.
    public long ExpiresAtUnix { get; set; }

    public bool IsNetworkWide => string.IsNullOrEmpty(ServerId);

    public bool IsActiveAt(long nowUnix)
        => ExpiresAtUnix <= 0 || nowUnix < ExpiresAtUnix;

    // True when this ban blocks the given backend. A network-wide ban blocks every backend;
    // a scoped ban only its own. An empty serverId asks "is the network itself blocked".
    public bool Blocks(string? serverId)
        => IsNetworkWide || string.Equals(ServerId, serverId, StringComparison.OrdinalIgnoreCase);
}

public sealed class BanRequest
{
    public string PlayerUid { get; set; } = "";
    public string PlayerName { get; set; } = "";

    // Empty for a network-wide ban.
    public string ServerId { get; set; } = "";
    public string Reason { get; set; } = "";
    public string BannedBy { get; set; } = "";

    // 0 or less means permanent.
    public int DurationSeconds { get; set; }
}

public sealed class BanResponse
{
    public bool Ok { get; set; }
    public NetworkBan? Ban { get; set; }
    public string? Error { get; set; }
}

public sealed class BanListResponse
{
    public bool Ok { get; set; }
    public List<NetworkBan> Bans { get; set; } = new();
}
