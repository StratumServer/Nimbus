using Nimbus.Shared.Models;

namespace Nimbus.Proxy.Tests;

/// <summary>Scripted IRegistryClient for router, status and ban-cache tests: serves what it was
/// handed, or throws. <see cref="Bans"/> null means "registry error" for the ban list, and
/// <see cref="Whitelist"/> null the same for the whitelist.</summary>
internal sealed class FakeRegistryClient : IRegistryClient
{
    public NetworkSnapshot? Snapshot;
    public bool Throw;
    public List<NetworkBan>? Bans;
    public int BanFetches;
    public List<WhitelistEntry>? Whitelist;
    public int WhitelistFetches;

    public Task<NetworkSnapshot?> GetServersAsync(CancellationToken ct, bool forceRefresh = false)
        => Throw
            ? throw new InvalidOperationException("registry down")
            : Task.FromResult(Snapshot);

    public Task<TransferReservation?> MintReservationAsync(string playerUid, string playerName,
        string targetServerId, string? reason, CancellationToken ct,
        string? realRemoteIp = null, int realRemotePort = 0, string? clientTransferId = null)
        => throw new NotSupportedException("not used by these tests");

    public Task<BackendSnapshot?> ResolveByServerIdAsync(string serverId, CancellationToken ct)
        => throw new NotSupportedException("not used by these tests");

    public Task<List<TransferIntent>> DrainTransferIntentsAsync(CancellationToken ct)
        => throw new NotSupportedException("not used by these tests");

    public Task<List<NetworkBan>?> GetBansAsync(CancellationToken ct)
    {
        BanFetches++;
        return Throw
            ? throw new InvalidOperationException("registry down")
            : Task.FromResult(Bans);
    }

    public Task<NetworkBan?> AddBanAsync(BanRequest request, CancellationToken ct)
        => throw new NotSupportedException("not used by these tests");

    public Task<bool> LiftBanAsync(string playerUid, string? serverId, CancellationToken ct)
        => throw new NotSupportedException("not used by these tests");

    public Task<List<WhitelistEntry>?> GetWhitelistAsync(CancellationToken ct)
    {
        WhitelistFetches++;
        return Throw
            ? throw new InvalidOperationException("registry down")
            : Task.FromResult(Whitelist);
    }

    public Task<WhitelistEntry?> AddWhitelistAsync(WhitelistRequest request, CancellationToken ct)
        => throw new NotSupportedException("not used by these tests");

    public Task<bool> RemoveWhitelistAsync(string playerUid, string? serverId, CancellationToken ct)
        => throw new NotSupportedException("not used by these tests");
}
