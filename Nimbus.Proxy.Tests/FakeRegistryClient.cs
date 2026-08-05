using Nimbus.Shared.Models;

namespace Nimbus.Proxy.Tests;

/// <summary>Scripted IRegistryClient for the router, status, cache, admin and dispatcher tests:
/// serves what it was handed, or throws. <see cref="Bans"/> null means "registry error" for the
/// ban list, and <see cref="Whitelist"/> null the same for the whitelist. The write calls answer
/// from the matching <c>...Result</c> field and record what they were asked for, so a test can
/// script a refusal as easily as a success and then read back the exact request a command
/// built.</summary>
internal sealed class FakeRegistryClient : IRegistryClient
{
    public NetworkSnapshot? Snapshot;
    public bool Throw;
    public List<NetworkBan>? Bans;
    public int BanFetches;
    public List<WhitelistEntry>? Whitelist;
    public int WhitelistFetches;

    /// <summary>What AddBanAsync answers. Null is "the registry refused the ban".</summary>
    public NetworkBan? AddBanResult;
    public BanRequest? LastBanRequest;

    /// <summary>What LiftBanAsync answers, and the pair it was last called with.</summary>
    public bool LiftBanResult;
    public (string Uid, string? ServerId)? LastLift;

    /// <summary>What AddWhitelistAsync answers. Null is "the registry refused the entry".</summary>
    public WhitelistEntry? AddWhitelistResult;
    public WhitelistRequest? LastWhitelistRequest;

    /// <summary>What RemoveWhitelistAsync answers, and the pair it was last called with.</summary>
    public bool RemoveWhitelistResult;
    public (string Uid, string? ServerId)? LastWhitelistRemove;

    /// <summary>Intents handed to the next drain, and how many drains have happened.</summary>
    public List<TransferIntent> Intents = new();
    public int Drains;

    /// <summary>Backends ResolveByServerIdAsync knows about, keyed by serverId.</summary>
    public Dictionary<string, BackendSnapshot> Backends = new(StringComparer.OrdinalIgnoreCase);

    public Task<NetworkSnapshot?> GetServersAsync(CancellationToken ct, bool forceRefresh = false)
        => Throw
            ? throw new InvalidOperationException("registry down")
            : Task.FromResult(Snapshot);

    public Task<TransferReservation?> MintReservationAsync(string playerUid, string playerName,
        string targetServerId, string? reason, CancellationToken ct,
        string? realRemoteIp = null, int realRemotePort = 0, string? clientTransferId = null)
        => Task.FromResult<TransferReservation?>(new TransferReservation
        {
            Id = "fake-reservation",
            PlayerUid = playerUid,
            PlayerName = playerName,
            TargetServerId = targetServerId,
            ExpiresAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 60,
        });

    public Task<BackendSnapshot?> ResolveByServerIdAsync(string serverId, CancellationToken ct)
        => Task.FromResult(Backends.TryGetValue(serverId, out var b) ? b : null);

    public Task<List<TransferIntent>> DrainTransferIntentsAsync(CancellationToken ct)
    {
        Drains++;
        if (Throw) throw new InvalidOperationException("registry down");
        var drained = Intents;
        Intents = new List<TransferIntent>();
        return Task.FromResult(drained);
    }

    public Task<List<NetworkBan>?> GetBansAsync(CancellationToken ct)
    {
        BanFetches++;
        return Throw
            ? throw new InvalidOperationException("registry down")
            : Task.FromResult(Bans);
    }

    public Task<NetworkBan?> AddBanAsync(BanRequest request, CancellationToken ct)
    {
        LastBanRequest = request;
        return Task.FromResult(AddBanResult);
    }

    public Task<bool> LiftBanAsync(string playerUid, string? serverId, CancellationToken ct)
    {
        LastLift = (playerUid, serverId);
        return Task.FromResult(LiftBanResult);
    }

    public Task<List<WhitelistEntry>?> GetWhitelistAsync(CancellationToken ct)
    {
        WhitelistFetches++;
        return Throw
            ? throw new InvalidOperationException("registry down")
            : Task.FromResult(Whitelist);
    }

    public Task<WhitelistEntry?> AddWhitelistAsync(WhitelistRequest request, CancellationToken ct)
    {
        LastWhitelistRequest = request;
        return Task.FromResult(AddWhitelistResult);
    }

    public Task<bool> RemoveWhitelistAsync(string playerUid, string? serverId, CancellationToken ct)
    {
        LastWhitelistRemove = (playerUid, serverId);
        return Task.FromResult(RemoveWhitelistResult);
    }
}
