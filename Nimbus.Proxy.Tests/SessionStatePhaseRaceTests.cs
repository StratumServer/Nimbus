using Xunit;

namespace Nimbus.Proxy.Tests;

// Regression cover for issue #106: SessionState.phase was a plain field written by both sniffer
// pumps and read from other threads. The transition in OnFrame is a read-modify-write (the
// NextPhase* tables gate on the current phase), so two concurrent pumps could read the same phase,
// decide two different successors, and one store would clobber the other. These tests hammer both
// directions at once and pin an invariant that only a serialised transition can hold.
public class SessionStatePhaseRaceTests
{
    // The two phase-gated arms of the state machine are the client and server Identification
    // frames: client Identification acks TcpOpen -> IdentSent, server Identification acks
    // TcpOpen or IdentSent -> IdentAcked. Race them on a fresh state per round.
    //
    // Every serial ordering of the two frames ends at IdentAcked. Client-then-server walks
    // TcpOpen -> IdentSent -> IdentAcked; server-then-client goes straight to IdentAcked and the
    // later client frame, seeing IdentAcked, changes nothing. So a final phase of IdentSent is
    // unreachable by any interleaving of the two calls: it can only appear when the client frame
    // decides IdentSent off a stale TcpOpen it read before the server's IdentAcked landed, then
    // writes it back over that ack. That lost read-modify-write is exactly what the lock prevents.
    [Fact]
    public async Task OnFrame_RacingHandshakeFromBothPumps_NeverLosesTheServerAck()
    {
        const int rounds = 40_000;

        var states = new SessionState[rounds];
        for (int i = 0; i < rounds; i++)
            states[i] = new SessionState(i);

        // Release both pumps into the same round at the same instant to maximise the overlap of
        // their read windows.
        var start = new Barrier(2);

        var clientPump = Task.Run(() =>
        {
            for (int i = 0; i < rounds; i++)
            {
                start.SignalAndWait();
                states[i].OnFrame(clientToServer: true, "Identification");
            }
        });

        var serverPump = Task.Run(() =>
        {
            for (int i = 0; i < rounds; i++)
            {
                start.SignalAndWait();
                states[i].OnFrame(clientToServer: false, "Identification");
            }
        });

        await Task.WhenAll(clientPump, serverPump);

        int stuckAtIdentSent = 0;
        for (int i = 0; i < rounds; i++)
        {
            if (states[i].Current == SessionState.Phase.IdentSent)
                stuckAtIdentSent++;
        }

        Assert.Equal(0, stuckAtIdentSent);
        Assert.All(states, s => Assert.Equal(SessionState.Phase.IdentAcked, s.Current));
    }

    // Companion to the invariant above: while the two pumps drive full handshakes, a third thread
    // reads Current the way the admin and plugin transfer gate does, and every value it observes
    // is a defined Phase. This is a non-regression guard on the read path, not proof of the
    // visibility fix: an aligned 32-bit enum read never tears on the CLR regardless of volatile,
    // so this assertion holds with or without the keyword. Memory visibility is close to
    // untestable here; volatile stays correct and load-bearing for Current's lock-free readers,
    // and the lost-update half is what the racing test above actually proves.
    [Fact]
    public async Task Current_ReadWhileBothPumpsDrive_IsAlwaysADefinedPhase()
    {
        const int rounds = 20_000;

        var states = new SessionState[rounds];
        for (int i = 0; i < rounds; i++)
            states[i] = new SessionState(i);

        int cursor = -1;
        var start = new Barrier(2);

        var clientPump = Task.Run(() =>
        {
            for (int i = 0; i < rounds; i++)
            {
                Volatile.Write(ref cursor, i);
                start.SignalAndWait();
                states[i].OnFrame(clientToServer: true, "Identification");
                states[i].OnFrame(clientToServer: true, "RequestJoin");
                states[i].OnFrame(clientToServer: true, "Leave");
            }
        });

        var serverPump = Task.Run(() =>
        {
            for (int i = 0; i < rounds; i++)
            {
                start.SignalAndWait();
                states[i].OnFrame(clientToServer: false, "Identification");
                states[i].OnFrame(clientToServer: false, "LevelInitialize");
                states[i].OnFrame(clientToServer: false, "ServerReady");
            }
        });

        var badReads = 0;
        var reader = Task.Run(() =>
        {
            while (!clientPump.IsCompleted || !serverPump.IsCompleted)
            {
                int i = Volatile.Read(ref cursor);
                if (i < 0)
                    continue;

                SessionState.Phase seen = states[i].Current;
                if (!Enum.IsDefined(seen))
                    Interlocked.Increment(ref badReads);
            }
        });

        await Task.WhenAll(clientPump, serverPump, reader);

        Assert.Equal(0, badReads);
    }
}
