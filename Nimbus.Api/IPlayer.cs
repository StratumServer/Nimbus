namespace Nimbus.Proxy;

public interface IPlayer
{
    long Id { get; }
    string? Uid { get; }
    string? Name { get; }
    string ClientRemote { get; }
    IServerInfo? CurrentServer { get; }
    bool SupportsSeamlessTransfers { get; }

    Task<string?> TransferAsync(IServerInfo target, string? reason = null);
    Task<string?> TransferAsync(IServerInfo target, string mode, string? reason = null);
    void Disconnect(string? reason = null);
}
