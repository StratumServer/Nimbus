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

// Fires when a player sends a chat message, once per utterance, as seen on the wire between the
// client and its backend. Read-only on purpose: handlers observe, they do not edit or cancel.
//
// Nimbus routes bytes and does not author game content, so there is no proxy-side way to change
// or inject chat. Plugins get logging, moderation triggers, and outbound bridges (Discord, a web
// feed); a mod that needs to write into chat does it backend-side with the real chat API.
//
// Only the client-to-server direction is surfaced. That is where an utterance appears exactly
// once, whereas the server-to-client copy of the same line arrives once per recipient session,
// which would make a relay fan out messages by player count.
public sealed class PlayerChatEvent : ProxyEvent
{
    public IPlayer Player { get; }

    // The backend the player was on when they said it, null if not connected yet.
    public IServerInfo? Server { get; }

    public string Message { get; }

    // Vintage Story chat group the line was sent to (general, a private group, ...).
    public int GroupId { get; }

    public PlayerChatEvent(IPlayer player, IServerInfo? server, string message, int groupId)
    {
        Player = player;
        Server = server;
        Message = message;
        GroupId = groupId;
    }
}
