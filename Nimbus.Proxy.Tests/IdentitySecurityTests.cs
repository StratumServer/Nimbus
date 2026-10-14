using Xunit;

namespace Nimbus.Proxy.Tests;

public class IdentitySecurityTests
{
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
