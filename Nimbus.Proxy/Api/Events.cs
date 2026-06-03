namespace Nimbus.Proxy;

// Event handlers can mutate settable properties before the proxy acts on them.
public abstract class ProxyEvent { }

// Fired after the client TCP is accepted but before any backend connect attempt.
// Handlers can call Deny(reason) to refuse the connection (the proxy will close the socket).
public sealed class PlayerConnectEvent : ProxyEvent
{
    public IPlayer Player { get; }
    public bool IsDenied { get; private set; }
    public string? DenyReason { get; private set; }

    public PlayerConnectEvent(IPlayer player) { Player = player; }

    public void Deny(string reason) { IsDenied = true; DenyReason = reason; }
}

// Fired right before the proxy opens the first upstream for a session.
// Plugins can pick a different first target or cancel the login.
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

// Fired before each upstream connect attempt (initial connect and every transfer).
// Handlers may swap Target to redirect, or call Cancel(reason) to abort the connect.
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

// Fired after a successful upstream connect (initial or transfer).
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

// Fired after the player's session has been closed.
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
