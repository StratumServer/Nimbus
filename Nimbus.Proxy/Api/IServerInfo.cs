namespace Nimbus.Proxy;

// Read-only view of a backend known to the proxy. Wraps BackendEndpoint plus a stable
// serverId that plugins can use as a routing key.
public interface IServerInfo
{
    string ServerId { get; }
    string Host { get; }
    int Port { get; }
}

internal sealed class ServerInfo : IServerInfo
{
    public string ServerId { get; init; } = "";
    public string Host { get; init; } = "";
    public int Port { get; init; }

    public static ServerInfo From(BackendEndpoint b) => new()
    {
        ServerId = b.ServerId ?? "",
        Host = b.Host,
        Port = b.Port,
    };

    public BackendEndpoint ToEndpoint() => new() { Host = Host, Port = Port, ServerId = ServerId };

    public override string ToString() => string.IsNullOrEmpty(ServerId) ? $"{Host}:{Port}" : $"{ServerId} ({Host}:{Port})";
}
