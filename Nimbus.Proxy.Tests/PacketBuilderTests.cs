using Xunit;

namespace Nimbus.Proxy.Tests;

/// <summary>
/// Decodes the frames forged for vanilla clients (redirect, disconnect, query answer)
/// with an independent protobuf reader; these bytes are exactly what a real client's
/// parser sees, so the structure has to be right down to the field numbers.
/// </summary>
public class PacketBuilderTests
{
    [Fact]
    public void RedirectFrame_IsAWellFormedServerRedirect()
    {
        byte[] frame = RedirectBuilder.BuildRedirectFrame("play.example.com:42421", "Hub 2");

        var (compressed, _, payload) = ProtoWire.ParseFrame(frame);
        Assert.False(compressed, "forged frames must not carry the compressed flag");

        var envelope = ProtoWire.ReadFields(payload);
        Assert.Equal(29uL, ProtoWire.Single(envelope, 90).Varint);         // Packet_Server.Id = 29

        var redirect = ProtoWire.ReadFields(ProtoWire.Single(envelope, 29).Bytes);
        Assert.Equal("Hub 2", ProtoWire.Utf8(ProtoWire.Single(redirect, 1)));
        Assert.Equal("play.example.com:42421", ProtoWire.Utf8(ProtoWire.Single(redirect, 2)));
    }

    [Fact]
    public void RedirectFrame_RequiresAHost_AndToleratesANullName()
    {
        Assert.ThrowsAny<ArgumentException>(() => RedirectBuilder.BuildRedirectFrame("", "name"));

        byte[] frame = RedirectBuilder.BuildRedirectFrame("host:1", null!);
        var (_, _, payload) = ProtoWire.ParseFrame(frame);
        var redirect = ProtoWire.ReadFields(ProtoWire.Single(ProtoWire.ReadFields(payload), 29).Bytes);
        Assert.Equal("", ProtoWire.Utf8(ProtoWire.Single(redirect, 1)));
    }

    [Fact]
    public void DisconnectFrame_IsAWellFormedDisconnectPlayer()
    {
        byte[] frame = DisconnectBuilder.BuildDisconnectFrame("maintenance in 5 minutes");

        var (compressed, _, payload) = ProtoWire.ParseFrame(frame);
        Assert.False(compressed);

        var envelope = ProtoWire.ReadFields(payload);
        Assert.Equal(9uL, ProtoWire.Single(envelope, 90).Varint);          // Packet_Server.Id = 9

        var disconnect = ProtoWire.ReadFields(ProtoWire.Single(envelope, 8).Bytes);
        Assert.Equal("maintenance in 5 minutes", ProtoWire.Utf8(ProtoWire.Single(disconnect, 1)));
    }

    [Fact]
    public void QueryAnswerFrame_CarriesTheStatus_AndOmitsEmptyFields()
    {
        var status = new ServerQueryStatus(
            Name: "Nimbus Network",
            Motd: "behind one address",
            PlayerCount: 5,
            MaxPlayers: 32,
            GameMode: "survival",
            Password: true,
            ServerVersion: "1.22.0");

        byte[] frame = QueryAnswerBuilder.BuildFrame(status);

        var (compressed, _, payload) = ProtoWire.ParseFrame(frame);
        Assert.False(compressed);
        var envelope = ProtoWire.ReadFields(payload);
        Assert.Equal(28uL, ProtoWire.Single(envelope, 90).Varint);         // Packet_Server.Id = 28

        var answer = ProtoWire.ReadFields(ProtoWire.Single(envelope, 28).Bytes);
        Assert.Equal("Nimbus Network", ProtoWire.Utf8(ProtoWire.Single(answer, 1)));
        Assert.Equal("behind one address", ProtoWire.Utf8(ProtoWire.Single(answer, 2)));
        Assert.Equal(5uL, ProtoWire.Single(answer, 3).Varint);
        Assert.Equal(32uL, ProtoWire.Single(answer, 4).Varint);
        Assert.Equal("survival", ProtoWire.Utf8(ProtoWire.Single(answer, 5)));
        Assert.Equal(1uL, ProtoWire.Single(answer, 6).Varint);             // password flag
        Assert.Equal("1.22.0", ProtoWire.Utf8(ProtoWire.Single(answer, 7)));

        // Zero/empty/false fields are omitted entirely (proto3-style presence).
        byte[] bare = QueryAnswerBuilder.BuildFrame(new ServerQueryStatus("n", "", 0, 0, "", false, ""));
        var bareAnswer = ProtoWire.ReadFields(
            ProtoWire.Single(ProtoWire.ReadFields(ProtoWire.ParseFrame(bare).Payload), 28).Bytes);
        Assert.Single(bareAnswer); // only the name survives
        Assert.Equal(1, bareAnswer[0].Number);
    }

    [Theory]
    [InlineData(new byte[] { 0x08, 0x0F }, true)]  // bare packet id 15 (query)
    [InlineData(new byte[] { 0x52, 0x00 }, true)]  // query as empty field 10
    [InlineData(new byte[] { 0x08, 0x0E }, false)] // some other bare id
    [InlineData(new byte[] { 0x08, 0x0F, 0x00 }, false)] // right prefix, wrong length
    public void IsQueryFrame_RecognizesBothQueryShapes(byte[] payload, bool expected)
    {
        Assert.Equal(expected, QueryAnswerBuilder.IsQueryFrame(ProtoWire.Frame(payload)));
    }

    [Fact]
    public void IsQueryFrame_RejectsCompressedAndLengthMismatchedFrames()
    {
        byte[] query = { 0x08, 0x0F };
        Assert.False(QueryAnswerBuilder.IsQueryFrame(ProtoWire.Frame(query, compressed: true)));

        byte[] tooLong = ProtoWire.Frame(query).Concat(new byte[] { 0x00 }).ToArray();
        Assert.False(QueryAnswerBuilder.IsQueryFrame(tooLong));
    }
}
