using Nimbus.Shared.Models;

namespace Nimbus.Proxy;

// Abstraction over the Nimbus registry. Two implementations:
//   * HttpRegistryClient: signed HTTP calls to a standalone Nimbus.Registry process.
//   * InProcRegistryClient: direct calls into the embedded registry services hosted inside
//     this proxy process. No HTTP round-trip, no HMAC.
//
// Callers depend on this interface so the same swap / dispatch / admin paths work in both
// embedded and remote registry modes.
internal interface IRegistryClient
{
    Task<TransferReservation?> MintReservationAsync(
        string playerUid, string playerName, string targetServerId, string? reason, CancellationToken ct,
        string? realRemoteIp = null, int realRemotePort = 0, string? clientTransferId = null);

    Task<NetworkSnapshot?> GetServersAsync(CancellationToken ct, bool forceRefresh = false);

    Task<BackendSnapshot?> ResolveByServerIdAsync(string serverId, CancellationToken ct);

    Task<List<TransferIntent>> DrainTransferIntentsAsync(CancellationToken ct);

    // Network bans. The proxy keeps a local cache of these (BanCache) because the connection
    // gate cannot afford a registry round-trip per join; these calls are the refresh and the
    // write path behind the admin commands.
    Task<List<NetworkBan>?> GetBansAsync(CancellationToken ct);

    Task<NetworkBan?> AddBanAsync(BanRequest request, CancellationToken ct);

    Task<bool> LiftBanAsync(string playerUid, string? serverId, CancellationToken ct);

    // Network whitelist. Cached proxy-side (WhitelistCache) for the same reason bans are: the
    // connection gate runs on the byte pump and cannot wait on the registry.
    Task<List<WhitelistEntry>?> GetWhitelistAsync(CancellationToken ct);

    Task<WhitelistEntry?> AddWhitelistAsync(WhitelistRequest request, CancellationToken ct);

    Task<bool> RemoveWhitelistAsync(string playerUid, string? serverId, CancellationToken ct);
}
