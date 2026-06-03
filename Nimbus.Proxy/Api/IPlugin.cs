namespace Nimbus.Proxy;

// Plugin entry point. Implementors are discovered by PluginLoader scanning plugins/*.dll
// at startup. Initialize runs once on the main thread before the listener accepts traffic.
public interface IPlugin
{
    string Name { get; }
    string Version => "0.0.0";

    void Initialize(IProxyApi api);

    // Called when the proxy is shutting down. Override to release resources.
    void Shutdown() { }
}

// Facade plugins use to interact with the proxy.
public interface IProxyApi
{
    EventBus Events { get; }

    // Live sessions, snapshotted on read. Safe to iterate concurrently with session churn.
    IEnumerable<IPlayer> Players { get; }

    bool TryGetPlayer(long sessionId, out IPlayer player);
    IPlayer? FindPlayerByUid(string uid);
    IPlayer? FindPlayerByName(string name);

    // Resolve a backend by configured serverId. Returns null when the registry doesn't know it
    // (or when Nimbus is disabled and the server isn't in the static pool).
    Task<IServerInfo?> ResolveServerAsync(string serverId, CancellationToken ct);

    void LogInfo(string pluginName, string message);
    void LogWarn(string pluginName, string message);
}
