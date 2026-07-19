using Nimbus.Shared.Models;

namespace Nimbus.Proxy.Tests;

/// <summary>Scripted IRegistryClient for router and status tests: serves a snapshot or throws.</summary>
internal sealed class FakeRegistryClient : IRegistryClient
{
    public NetworkSnapshot? Snapshot;
    public bool Throw;

    public Task<NetworkSnapshot?> GetServersAsync(CancellationToken ct, bool forceRefresh = false)
        => Throw
            ? throw new InvalidOperationException("registry down")
            : Task.FromResult(Snapshot);

    public Task<TransferReservation?> MintReservationAsync(string playerUid, string playerName,
        string targetServerId, string? reason, CancellationToken ct,
        string? realRemoteIp = null, int realRemotePort = 0)
        => throw new NotSupportedException("not used by these tests");

    public Task<BackendSnapshot?> ResolveByServerIdAsync(string serverId, CancellationToken ct)
        => throw new NotSupportedException("not used by these tests");

    public Task<List<TransferIntent>> DrainTransferIntentsAsync(CancellationToken ct)
        => throw new NotSupportedException("not used by these tests");
}
