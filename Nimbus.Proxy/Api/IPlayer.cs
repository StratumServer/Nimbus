namespace Nimbus.Proxy;

// Public, plugin-facing view of a proxied player.
public interface IPlayer
{
    long Id { get; }
    string? Uid { get; }
    string? Name { get; }
    string ClientRemote { get; }
    IServerInfo? CurrentServer { get; }

    // Move the player to `target` using the proxy's default transfer mode.
    // Returns null on success or a short failure reason.
    Task<string?> TransferAsync(IServerInfo target, string? reason = null);

    // Move the player to `target` with an explicit mode ("redirect" or "seamless").
    // Returns null on success or a short failure reason.
    Task<string?> TransferAsync(IServerInfo target, string mode, string? reason = null);

    // Force-close this player's session.
    void Disconnect(string? reason = null);
}
