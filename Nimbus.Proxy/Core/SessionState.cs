namespace Nimbus.Proxy;

// Tracks the high-level state of a session by watching packet types in each direction.
// Logs every transition so the swap path can tell when a transfer is safe (Phase.Ready).
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

    // Called once per frame by the sniffer with the parsed packet name and direction.
    public void OnFrame(bool clientToServer, string packetName)
    {
        Phase next = phase;

        if (clientToServer)
        {
            if (packetName == "Identification" && phase == Phase.TcpOpen) next = Phase.IdentSent;
            else if (packetName.StartsWith("Id=11(", StringComparison.Ordinal)) next = Phase.JoinRequested; // RequestJoin (bare-Id form)
            else if (packetName == "RequestJoin") next = Phase.JoinRequested;
            else if (packetName == "ClientPlaying" || packetName.StartsWith("Id=29(", StringComparison.Ordinal)) next = Phase.Ready;
            else if (packetName == "Leave" || packetName.StartsWith("Id=14(", StringComparison.Ordinal)) next = Phase.Disconnecting;
        }
        else
        {
            if (packetName == "Identification" && (phase == Phase.TcpOpen || phase == Phase.IdentSent)) next = Phase.IdentAcked;
            else if (packetName == "LevelInitialize") next = Phase.LevelLoading;
            else if (packetName == "ServerReady" || packetName.StartsWith("Id=73(", StringComparison.Ordinal)) next = Phase.Ready;
            else if (packetName == "DisconnectPlayer" || packetName.StartsWith("Id=9(", StringComparison.Ordinal)) next = Phase.Disconnecting;
            else if (packetName == "Redirect" || packetName.StartsWith("Id=29(", StringComparison.Ordinal))
                Log.Warn($"[s{sessionId}] backend sent ServerRedirect through proxy, this would crash the client; the swap path must intercept");
        }

        if (next != phase)
        {
            Log.Info($"[s{sessionId}] phase {phase} -> {next}  (trigger: {(clientToServer ? "c->s" : "s->c")} {packetName})");
            phase = next;
        }
    }
}
