namespace Nimbus.Proxy;

public abstract class ProxyEvent { }

public sealed class PlayerConnectEvent : ProxyEvent
{
    public IPlayer Player { get; }
    public bool IsDenied { get; private set; }
    public string? DenyReason { get; private set; }

    public PlayerConnectEvent(IPlayer player) { Player = player; }

    public void Deny(string reason) { IsDenied = true; DenyReason = reason; }
}

public sealed class PlayerChooseInitialServerEvent : ProxyEvent
{
    public IPlayer Player { get; }
    public IServerInfo? Target { get; set; }
    public bool IsCancelled { get; private set; }
    public string? CancelReason { get; private set; }

    public PlayerChooseInitialServerEvent(IPlayer player, IServerInfo? target)
    {
        Player = player;
        Target = target;
    }

    public void Cancel(string reason) { IsCancelled = true; CancelReason = reason; }
}

public sealed class ServerPreConnectEvent : ProxyEvent
{
    public IPlayer Player { get; }
    public IServerInfo Original { get; }
    public IServerInfo Target { get; set; }
    public string? Reason { get; }
    public bool IsCancelled { get; private set; }
    public string? CancelReason { get; private set; }

    public ServerPreConnectEvent(IPlayer player, IServerInfo target, string? reason)
    {
        Player = player;
        Original = target;
        Target = target;
        Reason = reason;
    }

    public void Cancel(string reason) { IsCancelled = true; CancelReason = reason; }
}

public sealed class ServerPostConnectEvent : ProxyEvent
{
    public IPlayer Player { get; }
    public IServerInfo Server { get; }
    public IServerInfo? Previous { get; }

    public ServerPostConnectEvent(IPlayer player, IServerInfo server, IServerInfo? previous)
    {
        Player = player;
        Server = server;
        Previous = previous;
    }
}

public sealed class PlayerDisconnectEvent : ProxyEvent
{
    public IPlayer Player { get; }
    public long BytesC2S { get; }
    public long BytesS2C { get; }

    public PlayerDisconnectEvent(IPlayer player, long bytesC2S, long bytesS2C)
    {
        Player = player;
        BytesC2S = bytesC2S;
        BytesS2C = bytesS2C;
    }
}

// Fires when the backend terminates a live (Phase.Ready or Disconnecting) player session
// rather than the player or proxy initiating the close. Allows plugins to react to kicks,
// backend crashes, or unexpected drops distinctly from voluntary disconnects.
public sealed class ServerKickedEvent : ProxyEvent
{
    public IPlayer Player { get; }
    public IServerInfo Server { get; }

    public ServerKickedEvent(IPlayer player, IServerInfo server)
    {
        Player = player;
        Server = server;
    }
}

// Fires after a redirect or seamless transfer completes successfully from the proxy side.
public sealed class PlayerTransferredEvent : ProxyEvent
{
    public IPlayer Player { get; }
    public IServerInfo? From { get; }
    public IServerInfo To { get; }
    public string Mode { get; }

    public PlayerTransferredEvent(IPlayer player, IServerInfo? from, IServerInfo to, string mode)
    {
        Player = player;
        From = from;
        To = to;
        Mode = mode;
    }
}
