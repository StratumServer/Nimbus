namespace Nimbus.Shared.Models;

// Aggregated registry snapshot returned by GET /api/servers.
public sealed class NetworkSnapshot
{
    public List<BackendSnapshot> Backends { get; set; } = new();
    public int TotalPlayers { get; set; }
    public int TotalCapacity { get; set; }
    public long GeneratedAtUnix { get; set; }
}

public sealed class BackendSnapshot
{
    public string ServerId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string PublicHost { get; set; } = "";
    public int PublicPort { get; set; }
    public string[] Tags { get; set; } = Array.Empty<string>();
    public int Players { get; set; }
    public int MaxPlayers { get; set; }
    public double Tps { get; set; }
    public bool Maintenance { get; set; }
    public bool ReservationRequired { get; set; }
    public long LastSeenUnix { get; set; }
    public bool Stale { get; set; }
    public string StratumVersion { get; set; } = "";
    public string GameVersion { get; set; } = "";
}
