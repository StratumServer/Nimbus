using Nimbus.Shared.Models;
using System.Net.Sockets;

namespace Nimbus.Proxy;

internal sealed class ServerStatusResponder
{
    private readonly ProxyConfig cfg;
    private readonly IRegistryClient? registry;
    private readonly Func<int> activeSessions;
    private readonly CancellationToken stopToken;

    public ServerStatusResponder(ProxyConfig cfg, IRegistryClient? registry, Func<int> activeSessions, CancellationToken stopToken)
    {
        this.cfg = cfg;
        this.registry = registry;
        this.activeSessions = activeSessions;
        this.stopToken = stopToken;
    }

    public async Task<bool> TryHandleAsync(TcpClient client, ReadOnlyMemory<byte> firstFrame)
    {
        if (!cfg.Status.Enabled || !QueryAnswerBuilder.IsQueryFrame(firstFrame))
            return false;

        var status = await BuildStatusAsync().ConfigureAwait(false);
        var answer = QueryAnswerBuilder.BuildFrame(status);
        try
        {
            using var writeCts = CancellationTokenSource.CreateLinkedTokenSource(stopToken);
            writeCts.CancelAfter(TimeSpan.FromSeconds(2));
            await client.GetStream().WriteAsync(answer, writeCts.Token).ConfigureAwait(false);
            await client.GetStream().FlushAsync(writeCts.Token).ConfigureAwait(false);
            try { await Task.Delay(100, writeCts.Token).ConfigureAwait(false); } catch { }
        }
        catch { }

        try { client.Close(); } catch { }
        return true;
    }

    private async Task<ServerQueryStatus> BuildStatusAsync()
    {
        int players = activeSessions();
        int maxPlayers = cfg.Status.MaxPlayers;
        string version = cfg.Status.ServerVersion;

        var snap = await TryGetSnapshotAsync().ConfigureAwait(false);
        if (snap != null)
        {
            players = snap.TotalPlayers;
            if (snap.TotalCapacity > 0) maxPlayers = snap.TotalCapacity;
            if (string.IsNullOrWhiteSpace(version)) version = FindVersion(snap);
        }

        return new ServerQueryStatus(
            cfg.Status.Name,
            cfg.Status.Motd,
            players,
            maxPlayers,
            cfg.Status.GameMode,
            cfg.Status.Password,
            version);
    }

    private async Task<NetworkSnapshot?> TryGetSnapshotAsync()
    {
        if (registry == null) return null;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(stopToken);
            cts.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(100, cfg.Status.QueryTimeoutMs)));
            return await registry.GetServersAsync(cts.Token).ConfigureAwait(false);
        }
        catch { return null; }
    }

    private static string FindVersion(NetworkSnapshot snap)
    {
        foreach (var backend in snap.Backends)
        {
            if (!string.IsNullOrWhiteSpace(backend.GameVersion))
                return backend.GameVersion;
        }
        return "";
    }
}
