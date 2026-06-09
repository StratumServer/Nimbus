namespace Nimbus.Proxy;

public interface IServerInfo
{
    string ServerId { get; }
    string Host { get; }
    int Port { get; }
}

public sealed class ServerInfo : IServerInfo
{
    public string ServerId { get; init; } = "";
    public string Host { get; init; } = "";
    public int Port { get; init; }

    public override string ToString()
        => string.IsNullOrEmpty(ServerId) ? $"{Host}:{Port}" : $"{ServerId} ({Host}:{Port})";
}
