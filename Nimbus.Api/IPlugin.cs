namespace Nimbus.Proxy;

public interface IPlugin
{
    string Name { get; }
    string Version => "0.0.0";

    void Initialize(IProxyApi api);
    void Shutdown() { }
}

public interface IPluginMetadata
{
    string Id { get; }
    string Name { get; }
    string Version { get; }
    string ApiVersion { get; }
    IReadOnlyList<string> Dependencies { get; }
}

public sealed record PluginMetadata(
    string Id,
    string Name,
    string Version,
    string ApiVersion,
    IReadOnlyList<string> Dependencies) : IPluginMetadata;

public interface IProxyApi
{
    EventBus Events { get; }
    IEnumerable<IPlayer> Players { get; }

    bool TryGetPlayer(long sessionId, out IPlayer player);
    IPlayer? FindPlayerByUid(string uid);
    IPlayer? FindPlayerByName(string name);

    Task<IServerInfo?> ResolveServerAsync(string serverId, CancellationToken ct);

    void LogInfo(string pluginName, string message);
    void LogWarn(string pluginName, string message);
}
