using Nimbus.Shared;
using Nimbus.Shared.Models;

namespace Nimbus.Proxy;

// Read-only network status served at /status on the metrics host, meant for game panels
// and dashboards. Merges the configured backend pool with the registry's health snapshot
// and the operator's drain flags; configured-but-silent backends still show up (with
// registered = false) so a panel can tell "not sending heartbeats" from "not configured".
internal sealed class StatusReport
{
    public bool Ok { get; init; } = true;
    public long GeneratedAtUnix { get; init; }
    public StatusProxy Proxy { get; init; } = new();
    public List<StatusBackend> Backends { get; init; } = new();
    public StatusTotals Totals { get; init; } = new();

    public static async Task<StatusReport> BuildAsync(
        ProxyConfig cfg, BackendRouter router, IRegistryClient? registry, long startUnix, CancellationToken ct)
    {
        NetworkSnapshot? snap = null;
        if (registry != null)
        {
            // Best-effort: a down registry must not take /status down with it. Backends
            // simply report registered = false until the snapshot is back.
            try { snap = await registry.GetServersAsync(ct).ConfigureAwait(false); }
            catch (Exception ex) { Log.Trace($"status: snapshot fetch failed: {ex.Message}"); }
        }

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var report = new StatusReport
        {
            GeneratedAtUnix = now,
            Proxy = new StatusProxy
            {
                Name = cfg.Status.Name,
                Version = NimbusProtocol.NimbusVersion,
                Protocol = NimbusProtocol.ProtocolVersion,
                UptimeSeconds = Math.Max(0, now - startUnix),
                ActiveSessions = ProxyMetrics.ActiveSessionCount,
                MaxPlayers = cfg.Status.MaxPlayers,
            },
        };

        foreach (var candidate in router.Candidates)
        {
            BackendSnapshot? b = null;
            if (snap != null && !string.IsNullOrEmpty(candidate.ServerId))
            {
                foreach (var x in snap.Backends)
                {
                    if (string.Equals(x.ServerId, candidate.ServerId, StringComparison.OrdinalIgnoreCase))
                    { b = x; break; }
                }
            }

            report.Backends.Add(new StatusBackend
            {
                ServerId = candidate.ServerId,
                Host = candidate.Host,
                Port = candidate.Port,
                Registered = b != null,
                Drained = router.IsDrained(candidate.ServerId),
                Stale = b?.Stale ?? false,
                Maintenance = b?.Maintenance ?? false,
                ReservationRequired = b?.ReservationRequired ?? false,
                Players = b?.Players ?? 0,
                MaxPlayers = b?.MaxPlayers ?? 0,
                GameVersion = b?.GameVersion ?? "",
                LastSeenUnix = b?.LastSeenUnix ?? 0,
            });

            if (b != null && !b.Stale)
            {
                report.Totals.Players += b.Players;
                report.Totals.Capacity += b.MaxPlayers;
            }
        }

        return report;
    }
}

internal sealed class StatusProxy
{
    public string Name { get; init; } = "";
    public string Version { get; init; } = "";
    public int Protocol { get; init; }
    public long UptimeSeconds { get; init; }
    public long ActiveSessions { get; init; }
    public int MaxPlayers { get; init; }
}

internal sealed class StatusBackend
{
    public string ServerId { get; init; } = "";
    public string Host { get; init; } = "";
    public int Port { get; init; }

    // False when the backend is configured on the proxy but has not heartbeated into the
    // registry (or there is no registry): health fields below are defaults in that case.
    public bool Registered { get; init; }
    public bool Drained { get; init; }
    public bool Stale { get; init; }
    public bool Maintenance { get; init; }
    public bool ReservationRequired { get; init; }
    public int Players { get; init; }
    public int MaxPlayers { get; init; }
    public string GameVersion { get; init; } = "";
    public long LastSeenUnix { get; init; }
}

internal sealed class StatusTotals
{
    public int Players { get; set; }
    public int Capacity { get; set; }
}
