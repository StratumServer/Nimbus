namespace Nimbus.Proxy;

// IProxyApi implementation backed by the live ProxyListener.
internal sealed class ProxyApi : IProxyApi
{
    private readonly ProxyListener proxy;

    public ProxyApi(ProxyListener proxy) { this.proxy = proxy; }

    public EventBus Events => proxy.Events;

    public IEnumerable<IPlayer> Players => proxy.Sessions.Values;

    public bool TryGetPlayer(long sessionId, out IPlayer player)
    {
        if (proxy.Sessions.TryGetValue(sessionId, out var s)) { player = s; return true; }
        player = null!;
        return false;
    }

    public IPlayer? FindPlayerByUid(string uid)
    {
        foreach (var s in proxy.Sessions.Values)
            if (string.Equals(s.PlayerUid, uid, StringComparison.OrdinalIgnoreCase)) return s;
        return null;
    }

    public IPlayer? FindPlayerByName(string name)
    {
        foreach (var s in proxy.Sessions.Values)
            if (string.Equals(s.PlayerName, name, StringComparison.OrdinalIgnoreCase)) return s;
        return null;
    }

    public async Task<IServerInfo?> ResolveServerAsync(string serverId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(serverId)) return null;
        if (proxy.Registry != null)
        {
            var b = await proxy.Registry.ResolveByServerIdAsync(serverId, ct).ConfigureAwait(false);
            if (b != null) return new ServerInfo { ServerId = serverId, Host = b.PublicHost, Port = b.PublicPort };
        }
        // Fall back to the static config pool so plugins still work without Nimbus enabled.
        foreach (var ep in proxy.Cfg.Backends())
            if (string.Equals(ep.ServerId, serverId, StringComparison.OrdinalIgnoreCase))
                return ServerInfo.From(ep);
        return null;
    }

    public void LogInfo(string pluginName, string message) => Log.Info($"[plugin {pluginName}] {message}");
    public void LogWarn(string pluginName, string message) => Log.Warn($"[plugin {pluginName}] {message}");
}
