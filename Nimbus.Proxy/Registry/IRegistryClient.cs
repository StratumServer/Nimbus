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
        string? realRemoteIp = null, int realRemotePort = 0);

    Task<NetworkSnapshot?> GetServersAsync(CancellationToken ct, bool forceRefresh = false);

    Task<BackendSnapshot?> ResolveByServerIdAsync(string serverId, CancellationToken ct);

    Task<List<TransferIntent>> DrainTransferIntentsAsync(CancellationToken ct);
}
