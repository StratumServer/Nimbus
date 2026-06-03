using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nimbus.Registry.Services;
using Nimbus.Shared.Models;

namespace Nimbus.Registry.MasterServer;

// Registers the network as a single entry on the VS master server, heartbeats while
// running, unregisters on shutdown. Disabled unless Identity.AdvertiseOnMasterServer
// is true and Identity.PublicHost is set.
internal sealed class MasterServerBroadcaster : BackgroundService
{
    private readonly RegistryConfig _cfg;
    private readonly BackendRegistry _backends;
    private readonly ILogger<MasterServerBroadcaster> _log;
    private MasterServerClient? _client;
    private string? _token;
    private int _lastRegisteredMaxPlayers = -1;

    public MasterServerBroadcaster(RegistryConfig cfg, BackendRegistry backends, ILogger<MasterServerBroadcaster> log)
    {
        _cfg = cfg;
        _backends = backends;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        var id = _cfg.Identity;
        if (!id.AdvertiseOnMasterServer)
        {
            _log.LogInformation("master server advertising disabled (Identity.AdvertiseOnMasterServer=false)");
            return;
        }
        if (string.IsNullOrWhiteSpace(id.PublicHost))
        {
            _log.LogWarning("master server advertising requested but Identity.PublicHost is empty. Set it to the proxy's public hostname.");
            return;
        }

        _client = new MasterServerClient(id.MasterServerUrl, _log);
        _log.LogInformation("master server advertising as '{Name}' at {Host}:{Port}", id.ServerName, id.PublicHost, id.PublicPort);

        // Wait up to 30s for at least one backend to heartbeat so the first register
        // packet carries a real maxPlayers and mod list.
        var waitUntil = DateTime.UtcNow.AddSeconds(30);
        while (!stop.IsCancellationRequested && DateTime.UtcNow < waitUntil)
        {
            var snap = _backends.Snapshot();
            if (snap.Backends.Any(b => !b.Stale) && (id.MaxPlayersOverride > 0 || snap.TotalCapacity > 0))
                break;
            try { await Task.Delay(TimeSpan.FromSeconds(2), stop); } catch (TaskCanceledException) { return; }
        }

        var heartbeatInterval = TimeSpan.FromSeconds(Math.Max(60, id.HeartbeatIntervalSeconds));

        while (!stop.IsCancellationRequested)
        {
            try
            {
                if (string.IsNullOrEmpty(_token))
                {
                    await TryRegister(stop);
                }
                else if (CapacityChanged())
                {
                    // Heartbeat cannot update maxPlayers. Force a re-register.
                    _log.LogInformation("backend capacity changed, re-registering");
                    _token = null;
                    await TryRegister(stop);
                }
                else
                {
                    await TryHeartbeat(stop);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "master server tick failed");
            }

            try { await Task.Delay(heartbeatInterval, stop); }
            catch (TaskCanceledException) { break; }
        }

        if (!string.IsNullOrEmpty(_token))
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await _client!.UnregisterAsync(new UnregisterPacket { token = _token }, cts.Token);
                _log.LogInformation("master server unregistered");
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "master server unregister failed (ignored at shutdown)");
            }
        }
    }

    private async Task TryRegister(CancellationToken ct)
    {
        var packet = BuildRegisterPacket();
        var resp = await _client!.RegisterAsync(packet, ct);
        if (resp == null) return;
        if (resp.status == "ok")
        {
            _token = resp.data;
            _lastRegisteredMaxPlayers = packet.maxPlayers;
            _log.LogInformation("master server registered ok (maxPlayers={Max})", packet.maxPlayers);
        }
        else if (resp.status == "blacklisted")
        {
            _log.LogWarning("master server says this network is blacklisted. Other clients can still connect directly.");
            _token = null;
        }
        else
        {
            _log.LogWarning("master server register rejected: {Status} {Data}", resp.status, resp.data);
            _token = null;
        }
    }

    private async Task TryHeartbeat(CancellationToken ct)
    {
        var resp = await _client!.HeartbeatAsync(new HeartbeatPacket
        {
            token = _token!,
            players = CurrentPlayerCount()
        }, ct);
        if (resp == null) return;
        if (resp.status == "invalid" || resp.status == "timeout")
        {
            _log.LogInformation("master server heartbeat says {Status}, will re-register", resp.status);
            _token = null;
        }
    }

    private RegisterRequestPacket BuildRegisterPacket()
    {
        var id = _cfg.Identity;
        var snap = _backends.Snapshot();
        return new RegisterRequestPacket
        {
            port = id.PublicPort,
            name = id.ServerName,
            icon = id.ServerIcon,
            playstyle = new PlaystylePacket { id = id.Playstyle.Id, langCode = id.Playstyle.LangCode },
            maxPlayers = (ushort)Math.Clamp(EffectiveMaxPlayers(snap, id), 0, ushort.MaxValue),
            gameVersion = id.GameVersion,
            hasPassword = id.HasPassword,
            Mods = BuildModList(snap, id),
            serverUrl = id.ServerUrl,
            gameDescription = id.ServerDescription,
            whitelisted = id.Whitelisted,
            vhIdentifier = id.VhIdentifier
        };
    }

    private int CurrentPlayerCount()
    {
        var snap = _backends.Snapshot();
        return snap.TotalPlayers;
    }

    private bool CapacityChanged()
    {
        if (_lastRegisteredMaxPlayers < 0) return false;
        var snap = _backends.Snapshot();
        int current = Math.Clamp(EffectiveMaxPlayers(snap, _cfg.Identity), 0, ushort.MaxValue);
        return current != _lastRegisteredMaxPlayers;
    }

    private static int EffectiveMaxPlayers(NetworkSnapshot snap, ServerIdentityConfig id)
    {
        if (id.MaxPlayersOverride > 0) return id.MaxPlayersOverride;
        return snap.TotalCapacity;
    }

    private static ModPacket[] BuildModList(NetworkSnapshot snap, ServerIdentityConfig id)
    {
        if (string.Equals(id.ModSource, "explicit", StringComparison.OrdinalIgnoreCase))
        {
            return id.ExplicitMods.Select(m => new ModPacket { id = m.Id, version = m.Version }).ToArray();
        }
        if (id.ModSource.StartsWith("backend:", StringComparison.OrdinalIgnoreCase))
        {
            var serverId = id.ModSource["backend:".Length..];
            var backend = snap.Backends.FirstOrDefault(b => string.Equals(b.ServerId, serverId, StringComparison.OrdinalIgnoreCase) && !b.Stale);
            if (backend == null) return Array.Empty<ModPacket>();
            return backend.RequiredClientMods.Select(m => new ModPacket { id = m.Id, version = m.Version }).ToArray();
        }
        // Aggregate: union by id across non-stale backends, keep highest version string.
        var union = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var b in snap.Backends)
        {
            if (b.Stale) continue;
            foreach (var m in b.RequiredClientMods)
            {
                if (string.IsNullOrWhiteSpace(m.Id)) continue;
                if (!union.TryGetValue(m.Id, out var existing) || string.CompareOrdinal(m.Version, existing) > 0)
                    union[m.Id] = m.Version ?? "";
            }
        }
        return union.Select(kv => new ModPacket { id = kv.Key, version = kv.Value }).ToArray();
    }
}
