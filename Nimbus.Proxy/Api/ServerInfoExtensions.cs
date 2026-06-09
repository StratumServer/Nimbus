namespace Nimbus.Proxy;

internal static class ServerInfoExtensions
{
    public static ServerInfo ToServerInfo(this BackendEndpoint endpoint) => new()
    {
        ServerId = endpoint.ServerId ?? "",
        Host = endpoint.Host,
        Port = endpoint.Port,
    };

    public static BackendEndpoint ToEndpoint(this IServerInfo server) => new()
    {
        Host = server.Host,
        Port = server.Port,
        ServerId = server.ServerId,
    };
}
