using System.Security.Cryptography;
using Nimbus.Registry.Services;
using Nimbus.Shared.Models;

namespace Nimbus.Proxy;

// Direct in-process bridge to the embedded registry services. Bypasses HTTP + HMAC because
// both sides are the same OS process. Mirrors the request handling in Nimbus.Registry.Core
// Endpoints (reservation mint, snapshot get, intent drain) to keep behavior identical
// between embedded and remote modes.
internal sealed class InProcRegistryClient : IRegistryClient
{
    private readonly BackendRegistry backends;
    private readonly ReservationStore reservations;
    private readonly TransferIntentStore intents;
    private readonly RegistryConfig cfg;

    public InProcRegistryClient(BackendRegistry backends, ReservationStore reservations,
        TransferIntentStore intents, RegistryConfig cfg)
    {
        this.backends = backends;
        this.reservations = reservations;
        this.intents = intents;
        this.cfg = cfg;
    }

    public Task<TransferReservation?> MintReservationAsync(
        string playerUid, string playerName, string targetServerId, string? reason, CancellationToken ct,
        string? realRemoteIp = null, int realRemotePort = 0)
    {
        if (string.IsNullOrEmpty(playerUid) || string.IsNullOrEmpty(targetServerId))
            return Task.FromResult<TransferReservation?>(null);

        // Target must exist (matches /api/reservations behavior). Unknown targets would just
        // produce an unconsumable reservation, so fail fast.
        if (backends.Get(targetServerId) == null)
        {
            Log.Warn($"in-proc registry: unknown target server '{targetServerId}'");
            return Task.FromResult<TransferReservation?>(null);
        }

        int ttl = cfg.ReservationTtlSeconds;
        if (ttl <= 0) ttl = 60;
        if (ttl > cfg.MaxReservationTtlSeconds) ttl = cfg.MaxReservationTtlSeconds;

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var r = new TransferReservation
        {
            Id = NewId(),
            PlayerUid = playerUid,
            PlayerName = playerName ?? "",
            SourceServerId = cfg.ProxyId,
            TargetServerId = targetServerId,
            ExpiresAtUnix = now + ttl,
            Reason = reason,
            RealRemoteIp = realRemoteIp ?? "",
            RealRemotePort = realRemotePort,
        };
        reservations.Add(r);
        return Task.FromResult<TransferReservation?>(r);
    }

    public Task<NetworkSnapshot?> GetServersAsync(CancellationToken ct, bool forceRefresh = false)
        => Task.FromResult<NetworkSnapshot?>(backends.Snapshot());

    public Task<BackendSnapshot?> ResolveByServerIdAsync(string serverId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(serverId)) return Task.FromResult<BackendSnapshot?>(null);
        var snap = backends.Snapshot();
        foreach (var b in snap.Backends)
            if (string.Equals(b.ServerId, serverId, StringComparison.OrdinalIgnoreCase))
                return Task.FromResult<BackendSnapshot?>(b);
        return Task.FromResult<BackendSnapshot?>(null);
    }

    public Task<List<TransferIntent>> DrainTransferIntentsAsync(CancellationToken ct)
        => Task.FromResult(intents.Drain());

    private static string NewId()
    {
        Span<byte> bytes = stackalloc byte[12];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes);
    }
}
