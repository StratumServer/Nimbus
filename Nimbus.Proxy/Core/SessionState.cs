namespace Nimbus.Proxy;

// Tracks the high-level state of a session by watching packet types in each direction, so the
// swap path can tell when a transfer is safe (Phase.Ready).
internal sealed class SessionState
{
    private readonly long sessionId;
    private Phase phase = Phase.TcpOpen;

    public SessionState(long sessionId) { this.sessionId = sessionId; }

    public Phase Current => phase;

    public enum Phase
    {
        TcpOpen,            // sockets connected, nothing seen on wire
        IdentSent,          // client sent Identification
        IdentAcked,         // server replied with ServerIdentification
        LevelLoading,       // server is streaming level/chunks (LevelInitialize seen)
        JoinRequested,      // client sent RequestJoin
        Ready,              // server sent ServerReady, player is in-game
        Disconnecting,      // DisconnectPlayer or Leave observed
    }

    // Called once per frame by the sniffer with the parsed packet name and direction. The two
    // directions are two separate transition tables that share nothing but this dispatch, so each
    // one lives in its own method below rather than behind the direction flag.
    public void OnFrame(bool clientToServer, string packetName)
    {
        Phase next = clientToServer ? NextPhaseFromClient(packetName) : NextPhaseFromServer(packetName);

        if (next != phase)
            phase = next;
    }

    // Client to server. A name that matches nothing here leaves the phase where it was.
    // Runs on the c->s pump for every parsed frame, so it stays a chain of comparisons against
    // the interned literals the dispatcher hands us and allocates nothing.
    private Phase NextPhaseFromClient(string packetName)
    {
        if (packetName == "Identification" && phase == Phase.TcpOpen) return Phase.IdentSent;
        if (packetName.StartsWith("Id=11(", StringComparison.Ordinal)) return Phase.JoinRequested; // RequestJoin (bare-Id form)
        if (packetName == "RequestJoin") return Phase.JoinRequested;
        if (packetName == "ClientPlaying" || packetName.StartsWith("Id=29(", StringComparison.Ordinal)) return Phase.Ready;
        if (packetName == "Leave" || packetName.StartsWith("Id=14(", StringComparison.Ordinal)) return Phase.Disconnecting;
        return phase;
    }

    // Server to client. Same contract as the client table, with one arm that only warns: a
    // ServerRedirect reaching the client through the proxy is a bug elsewhere, not a phase.
    private Phase NextPhaseFromServer(string packetName)
    {
        if (packetName == "Identification" && (phase == Phase.TcpOpen || phase == Phase.IdentSent)) return Phase.IdentAcked;
        if (packetName == "LevelInitialize") return Phase.LevelLoading;
        if (packetName == "ServerReady" || packetName.StartsWith("Id=73(", StringComparison.Ordinal)) return Phase.Ready;
        if (packetName == "DisconnectPlayer" || packetName.StartsWith("Id=9(", StringComparison.Ordinal)) return Phase.Disconnecting;
        if (packetName == "Redirect" || packetName.StartsWith("Id=29(", StringComparison.Ordinal))
            Log.Warn($"[s{sessionId}] backend sent ServerRedirect through proxy, this would crash the client; the swap path must intercept");
        return phase;
    }
}
