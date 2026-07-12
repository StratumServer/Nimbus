using Xunit;

namespace Nimbus.Proxy.Tests;

public class IdentificationParserTests
{
    private static byte[] IdentBody(string? name, string? uid, bool withNoise = false)
    {
        var body = new MemoryStream();
        if (withNoise)
        {
            // Unknown fields the parser must skip: a varint, a 64-bit and a 32-bit field.
            ProtoWire.WriteTag(body, 1, 0);
            ProtoWire.WriteVarint(body, 3uL);
            ProtoWire.WriteTag(body, 3, 1);
            body.Write(new byte[8]);
            ProtoWire.WriteTag(body, 4, 5);
            body.Write(new byte[4]);
        }
        if (name != null) ProtoWire.WriteString(body, 2, name);   // Playername
        if (uid != null) ProtoWire.WriteString(body, 6, uid);     // PlayerUID
        return body.ToArray();
    }

    private static byte[] EnvelopeFrame(byte[] identBody)
    {
        var envelope = new MemoryStream();
        ProtoWire.WriteBytes(envelope, 2, identBody); // Packet_Client field 2 = Identification
        return ProtoWire.Frame(envelope.ToArray());
    }

    [Fact]
    public void Extracts_FromThePacketClientEnvelope()
    {
        byte[] frame = EnvelopeFrame(IdentBody("alice", "uid-123"));

        Assert.True(IdentificationParser.TryExtract(frame, out string uid, out string name));
        Assert.Equal("uid-123", uid);
        Assert.Equal("alice", name);
    }

    [Fact]
    public void Extracts_FromAFlattenedIdentificationBody()
    {
        // Some forks flatten the envelope: the payload IS the Identification body.
        byte[] frame = ProtoWire.Frame(IdentBody("bob", "uid-456"));

        Assert.True(IdentificationParser.TryExtract(frame, out string uid, out string name));
        Assert.Equal("uid-456", uid);
        Assert.Equal("bob", name);
    }

    [Fact]
    public void SkipsUnknownFields_BeforeTheInterestingOnes()
    {
        byte[] frame = EnvelopeFrame(IdentBody("carol", "uid-789", withNoise: true));

        Assert.True(IdentificationParser.TryExtract(frame, out string uid, out string name));
        Assert.Equal("uid-789", uid);
        Assert.Equal("carol", name);
    }

    [Fact]
    public void UidAlone_IsEnough_NameIsBestEffort()
    {
        byte[] frame = EnvelopeFrame(IdentBody(name: null, uid: "uid-solo"));

        Assert.True(IdentificationParser.TryExtract(frame, out string uid, out string name));
        Assert.Equal("uid-solo", uid);
        Assert.Equal("", name);
    }

    [Fact]
    public void NameWithoutUid_Fails()
    {
        byte[] frame = EnvelopeFrame(IdentBody(name: "ghost", uid: null));

        Assert.False(IdentificationParser.TryExtract(frame, out _, out _));
    }

    [Fact]
    public void CompressedFrame_IsRejected()
    {
        var envelope = new MemoryStream();
        ProtoWire.WriteBytes(envelope, 2, IdentBody("dave", "uid-000"));
        byte[] frame = ProtoWire.Frame(envelope.ToArray(), compressed: true);

        Assert.False(IdentificationParser.TryExtract(frame, out _, out _));
    }

    [Fact]
    public void TruncatedFrame_IsRejected()
    {
        byte[] frame = EnvelopeFrame(IdentBody("erin", "uid-111"));
        byte[] truncated = frame[..(frame.Length - 3)]; // header promises more than is there

        Assert.False(IdentificationParser.TryExtract(truncated, out _, out _));
    }

    [Fact]
    public void TooShortToHoldAHeader_IsRejected()
    {
        Assert.False(IdentificationParser.TryExtract(new byte[] { 0, 0, 0 }, out _, out _));
    }
}
