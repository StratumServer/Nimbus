using System.Net;
using System.Net.Sockets;
using System.IO.Compression;
using Xunit;

namespace Nimbus.Proxy.Tests;

public class IdentitySecurityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LargerClientFrames_RequireAnAcknowledgementFromTheBackend(bool backendAcknowledges)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var backend = new TcpListener(IPAddress.Loopback, 0); backend.Start();
        using var front = new TcpListener(IPAddress.Loopback, 0); front.Start();
        var target = new BackendEndpoint { Host = "127.0.0.1", Port = ((IPEndPoint)backend.LocalEndpoint).Port };
        using var player = new TcpClient();
        await player.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)front.LocalEndpoint).Port, stop.Token);
        using var accepted = await front.AcceptTcpClientAsync(stop.Token);
        var cfg = new ProxyConfig(); cfg.Registry.Mode = "disabled";
        var session = new ProxySession(1, cfg, accepted, stop.Token);
        byte[] identity = ClientFrames.Identification("uid", "name");
        Task run = session.RunAsync(new[] { target }, identity);
        using var upstream = await backend.AcceptTcpClientAsync(stop.Token);
        try
        {
            await upstream.GetStream().ReadExactlyAsync(new byte[identity.Length], stop.Token);
            byte[] playing = ClientFrames.ClientPlaying();
            await player.GetStream().WriteAsync(playing, stop.Token);
            await upstream.GetStream().ReadExactlyAsync(new byte[playing.Length], stop.Token);
            if (backendAcknowledges)
            {
                byte[] ack = VsWire.BuildServerPacketFrame(1, 1, Array.Empty<byte>());
                await upstream.GetStream().WriteAsync(ack, stop.Token);
                await player.GetStream().ReadExactlyAsync(new byte[ack.Length], stop.Token);
            }
            using var payload = new MemoryStream();
            ProtoWire.WriteBytes(payload, 100, new byte[ClientSessionRunner.MaxFirstFrameSize]);
            byte[] frame = ProtoWire.Frame(payload.ToArray());
            if (backendAcknowledges)
            {
                await player.GetStream().WriteAsync(frame, stop.Token);
                await upstream.GetStream().ReadExactlyAsync(new byte[frame.Length], stop.Token);
            }
            else
            {
                await player.GetStream().WriteAsync(frame.AsMemory(0, 4), stop.Token);
                await run.WaitAsync(TimeSpan.FromSeconds(2));
            }
        }
        finally { session.Close(); await run.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    private static byte[] Compressed(byte[] payload)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output,
            CompressionLevel.Fastest, leaveOpen: true)) zlib.Write(payload);
        return ProtoWire.Frame(output.ToArray(), compressed: true);
    }

    [Fact]
    public async Task CompressedNonIdentityFrame_IsValidatedAndForwardedUnchanged()
    {
        byte[] frame = Compressed(ClientFrames.LoginTokenQuery()[4..]);
        var gate = new ClientFrameGate();
        int forwards = 0;
        await gate.ForwardAsync(frame, (bytes, identification) =>
        {
            Assert.False(identification);
            Assert.Equal(frame, bytes.ToArray());
            forwards++;
            return ValueTask.CompletedTask;
        });
        Assert.Equal(1, forwards);
    }

    [Fact]
    public async Task CompressedIdentification_IsRejectedBeforeForwarding()
    {
        byte[] frame = Compressed(ClientFrames.Identification("uid", "name")[4..]);
        var gate = new ClientFrameGate();
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await gate.ForwardAsync(frame, (_, _) => throw new Xunit.Sdk.XunitException("invalid identity was forwarded")));
    }

    [Fact]
    public async Task CompressedPreLoginFrame_CannotInflateBeyondTheHandshakeBound()
    {
        using var payload = new MemoryStream();
        ProtoWire.WriteBytes(payload, 100, new byte[ClientSessionRunner.MaxFirstFrameSize]);
        byte[] frame = Compressed(payload.ToArray());
        Assert.True(frame.Length < ClientSessionRunner.MaxFirstFrameSize);
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await new ClientFrameGate().ForwardAsync(frame, (_, _) => throw new Xunit.Sdk.XunitException("oversized inflation was forwarded")));
    }

    [Fact]
    public async Task BackendAuthentication_AllowsNormalLargerFrames()
    {
        bool authenticated = false;
        var gate = new ClientFrameGate(() => authenticated);
        await gate.ForwardAsync(ClientFrames.Identification("uid", "name"), (_, _) => ValueTask.CompletedTask);
        authenticated = true;
        using var payload = new MemoryStream();
        ProtoWire.WriteBytes(payload, 100, new byte[ClientSessionRunner.MaxFirstFrameSize]);
        byte[] frame = ProtoWire.Frame(payload.ToArray());
        int forwards = 0;
        await gate.ForwardAsync(frame, (bytes, identification) =>
        {
            Assert.False(identification);
            Assert.Equal(frame, bytes.ToArray());
            forwards++;
            return ValueTask.CompletedTask;
        });
        Assert.Equal(1, forwards);
    }

    [Fact]
    public void InvalidUtf8Uid_IsRejected()
    {
        byte[] frame = Envelope(body => ProtoWire.WriteBytes(body, 6, new byte[] { 0xff }));
        Assert.False(IdentificationParser.TryExtract(frame, out _, out _));
    }

    [Fact]
    public async Task SelfReportedIdentityCannotRaiseThePreAuthenticationAllocationLimit()
    {
        var gate = new ClientFrameGate();
        await gate.ForwardAsync(ClientFrames.Identification("uid", "name"), (_, _) => ValueTask.CompletedTask);
        byte[] oversizedHeader = [0, 2, 0, 0];
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await gate.ForwardAsync(oversizedHeader, (_, _) => ValueTask.CompletedTask));
    }

    private static byte[] Envelope(Action<MemoryStream> writeBody, bool idFirst = false)
    {
        using var body = new MemoryStream();
        writeBody(body);
        using var env = new MemoryStream();
        if (idFirst) { ProtoWire.WriteTag(env, 1, 0); ProtoWire.WriteVarint(env, 1); }
        ProtoWire.WriteBytes(env, 2, body.ToArray());
        return ProtoWire.Frame(env.ToArray());
    }

    [Theory]
    [InlineData(2)]
    [InlineData(6)]
    public void DuplicateIdentityField_IsRejected(int field)
    {
        byte[] frame = Envelope(body =>
        {
            ProtoWire.WriteString(body, 2, "allowed");
            ProtoWire.WriteString(body, 6, "allowed-uid");
            ProtoWire.WriteString(body, field, "replacement");
        });
        Assert.False(IdentificationParser.TryExtract(frame, out _, out _));
    }

    [Fact]
    public void UidFieldSix_IsUsed_WithAnUnknownStringField()
    {
        byte[] frame = Envelope(body =>
        {
            ProtoWire.WriteString(body, 2, "allowed");
            ProtoWire.WriteString(body, 100, "decoy");
            ProtoWire.WriteString(body, 6, "authenticated-uid");
        }, idFirst: true);
        Assert.True(IdentificationParser.TryExtract(frame, out string uid, out _));
        Assert.Equal("authenticated-uid", uid);
    }

    [Fact]
    public void MalformedTailAfterIdentity_IsRejected()
    {
        byte[] frame = Envelope(body =>
        {
            ProtoWire.WriteString(body, 2, "allowed");
            ProtoWire.WriteString(body, 6, "allowed-uid");
            body.WriteByte(0x80);
        });
        Assert.False(IdentificationParser.TryExtract(frame, out _, out _));
    }

    [Fact]
    public void DuplicateNestedIdentification_IsRejected()
    {
        byte[] original = ClientFrames.Identification("uid", "name");
        using var env = new MemoryStream();
        env.Write(original.AsSpan(4));
        env.Write(original.AsSpan(4));
        Assert.False(IdentificationParser.TryExtract(ProtoWire.Frame(env.ToArray()), out _, out _));
    }

    [Fact]
    public async Task FragmentedIdentification_ForwardsNothingBeforeTheCompleteGate()
    {
        byte[] frame = ClientFrames.Identification("uid", "name");
        var gate = new ClientFrameGate();
        int forwarded = 0;
        ValueTask Forward(ReadOnlyMemory<byte> bytes, bool identification)
        {
            Assert.True(identification);
            Assert.Equal(frame, bytes.ToArray());
            forwarded++;
            return ValueTask.CompletedTask;
        }
        for (int i = 0; i < frame.Length - 1; i++)
        {
            await gate.ForwardAsync(frame.AsMemory(i, 1), Forward);
            Assert.Equal(0, forwarded);
        }
        await gate.ForwardAsync(frame.AsMemory(frame.Length - 1), Forward);
        Assert.Equal(1, forwarded);
    }

    [Fact]
    public async Task CoalescedRepeatedIdentification_OnlyTheFirstIsForwarded()
    {
        byte[] frame = ClientFrames.Identification("uid", "name");
        byte[] coalesced = [.. frame, .. frame];
        var gate = new ClientFrameGate();
        int forwarded = 0;
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await gate.ForwardAsync(coalesced, (_, _) =>
            {
                forwarded++;
                return ValueTask.CompletedTask;
            }));
        Assert.Equal(1, forwarded);
    }

    [Fact]
    public async Task InvalidFragmentedIdentification_NeverReachesTheBackend()
    {
        byte[] frame = Envelope(body =>
        {
            ProtoWire.WriteString(body, 6, "allowed");
            ProtoWire.WriteString(body, 6, "blocked");
        });
        var gate = new ClientFrameGate();
        int forwarded = 0;
        ValueTask Forward(ReadOnlyMemory<byte> _, bool __) { forwarded++; return ValueTask.CompletedTask; }
        await gate.ForwardAsync(frame.AsMemory(0, frame.Length - 1), Forward);
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await gate.ForwardAsync(frame.AsMemory(frame.Length - 1), Forward));
        Assert.Equal(0, forwarded);
    }
}
