using Xunit;

namespace Nimbus.Proxy.Tests;

public class FrameSnifferTests
{
    private static (FrameSniffer Sniffer, List<(string Name, byte[] Frame)> Seen) Make()
    {
        var seen = new List<(string, byte[])>();
        var sniffer = new FrameSniffer(1, "c->s")
        {
            OnRawFrame = (name, frame) => seen.Add((name, frame.ToArray())),
        };
        return (sniffer, seen);
    }

    private static byte[] SomePayload() // arbitrary valid protobuf: field 1 varint 15
        => new byte[] { 0x08, 0x0F };

    [Fact]
    public void SingleFrame_IsReportedOnce_WithTheHeaderIncluded()
    {
        var (sniffer, seen) = Make();
        byte[] frame = ProtoWire.Frame(SomePayload());

        sniffer.OnBytes(frame);

        var only = Assert.Single(seen);
        Assert.Equal(frame, only.Frame);
    }

    [Fact]
    public void FrameSplitAcrossReads_IsReassembled()
    {
        var (sniffer, seen) = Make();
        byte[] frame = ProtoWire.Frame(SomePayload());

        foreach (byte b in frame) sniffer.OnBytes(new[] { b }); // worst case: 1 byte per read

        var only = Assert.Single(seen);
        Assert.Equal(frame, only.Frame);
    }

    [Fact]
    public void MultipleFramesInOneRead_AreAllReported_InOrder()
    {
        var (sniffer, seen) = Make();
        byte[] a = ProtoWire.Frame(new byte[] { 0x08, 0x01 });
        byte[] b = ProtoWire.Frame(new byte[] { 0x08, 0x02 });
        byte[] c = ProtoWire.Frame(new byte[] { 0x08, 0x03 });

        sniffer.OnBytes(a.Concat(b).Concat(c).ToArray());

        Assert.Equal(3, seen.Count);
        Assert.Equal(a, seen[0].Frame);
        Assert.Equal(b, seen[1].Frame);
        Assert.Equal(c, seen[2].Frame);
    }

    [Fact]
    public void ZeroLengthFrame_IsSkipped_AndTheNextFrameStillParses()
    {
        var (sniffer, seen) = Make();
        byte[] frame = ProtoWire.Frame(SomePayload());

        sniffer.OnBytes(new byte[4].Concat(frame).ToArray()); // 4 zero bytes = empty frame

        var only = Assert.Single(seen);
        Assert.Equal(frame, only.Frame);
    }

    [Fact]
    public void CompressedFrame_IsReported_WithoutInflating()
    {
        var (sniffer, seen) = Make();
        byte[] frame = ProtoWire.Frame(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, compressed: true);

        sniffer.OnBytes(frame);

        var only = Assert.Single(seen);
        Assert.Equal("<zlib>", only.Name);
        Assert.Equal(frame, only.Frame);
    }

    [Fact]
    public void OversizedFrame_MakesTheSnifferGiveUp_WithoutReporting()
    {
        var (sniffer, seen) = Make();
        // Header declares a payload beyond the 256 MB cap (bit 31 clear).
        byte[] oversized = { 0x7F, 0xFF, 0xFF, 0xFF, 0x01, 0x02 };

        sniffer.OnBytes(oversized);

        Assert.Empty(seen);
        // The buffer was dropped: a subsequent well-formed frame parses from scratch.
        byte[] frame = ProtoWire.Frame(SomePayload());
        sniffer.OnBytes(frame);
        Assert.Equal(frame, Assert.Single(seen).Frame);
    }

    [Fact]
    public void SessionState_FollowsTheHandshake_ToReadyThenDisconnecting()
    {
        var state = new SessionState(7);
        var c2s = new FrameSniffer(7, "c->s", state);
        var s2c = new FrameSniffer(7, "s->c", state);

        // The sniffer feeds SessionState with parsed packet names; drive the state machine
        // through the packet-name API directly for the transitions PacketDispatch produces.
        Assert.Equal(SessionState.Phase.TcpOpen, state.Current);
        state.OnFrame(clientToServer: true, "Identification");
        Assert.Equal(SessionState.Phase.IdentSent, state.Current);
        state.OnFrame(clientToServer: false, "Identification");
        Assert.Equal(SessionState.Phase.IdentAcked, state.Current);
        state.OnFrame(clientToServer: false, "LevelInitialize");
        Assert.Equal(SessionState.Phase.LevelLoading, state.Current);
        state.OnFrame(clientToServer: true, "RequestJoin");
        Assert.Equal(SessionState.Phase.JoinRequested, state.Current);
        state.OnFrame(clientToServer: false, "ServerReady");
        Assert.Equal(SessionState.Phase.Ready, state.Current);
        state.OnFrame(clientToServer: true, "Leave");
        Assert.Equal(SessionState.Phase.Disconnecting, state.Current);

        GC.KeepAlive(c2s);
        GC.KeepAlive(s2c);
    }

    [Fact]
    public void SessionState_ServerIdentification_OnlyAcksFromTheHandshakePhases()
    {
        var state = new SessionState(8);
        state.OnFrame(true, "Identification");
        state.OnFrame(false, "Identification");
        state.OnFrame(false, "ServerReady");
        Assert.Equal(SessionState.Phase.Ready, state.Current);

        // A late server Identification must not drag an in-game session backwards.
        state.OnFrame(false, "Identification");
        Assert.Equal(SessionState.Phase.Ready, state.Current);
    }
}
