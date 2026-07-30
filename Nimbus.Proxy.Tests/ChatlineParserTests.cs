using System.Text;
using Xunit;

namespace Nimbus.Proxy.Tests;

/// <summary>
/// Frames are built here with an independent encoder rather than with VsWire, so the parser is
/// tested against the wire format itself and not against its own writer. Layout mirrors
/// Packet_ChatLine in VintagestoryLib: 1 = Message, 2 = Groupid, 3 = ChatType, 4 = Data.
/// </summary>
public class ChatlineParserTests
{
    private const int ChatlineEnvelopeField = 4;

    private static void WriteVarint(List<byte> to, ulong value)
    {
        while (value >= 0x80)
        {
            to.Add((byte)(value | 0x80));
            value >>= 7;
        }
        to.Add((byte)value);
    }

    private static void WriteTag(List<byte> to, int field, int wireType)
        => WriteVarint(to, (ulong)((field << 3) | wireType));

    private static byte[] ChatlineBody(string message, int groupId = 0, int chatType = 0, string? data = null)
    {
        var body = new List<byte>();
        if (message != null)
        {
            var utf8 = Encoding.UTF8.GetBytes(message);
            WriteTag(body, 1, 2);
            WriteVarint(body, (ulong)utf8.Length);
            body.AddRange(utf8);
        }
        WriteTag(body, 2, 0);
        WriteVarint(body, (ulong)groupId);
        WriteTag(body, 3, 0);
        WriteVarint(body, (ulong)chatType);
        if (data != null)
        {
            var utf8 = Encoding.UTF8.GetBytes(data);
            WriteTag(body, 4, 2);
            WriteVarint(body, (ulong)utf8.Length);
            body.AddRange(utf8);
        }
        return body.ToArray();
    }

    private static byte[] Frame(byte[] envelopePayload, bool compressed = false)
    {
        int header = envelopePayload.Length | (compressed ? 1 << 31 : 0);
        var frame = new byte[4 + envelopePayload.Length];
        frame[0] = (byte)(header >> 24);
        frame[1] = (byte)(header >> 16);
        frame[2] = (byte)(header >> 8);
        frame[3] = (byte)header;
        envelopePayload.CopyTo(frame, 4);
        return frame;
    }

    private static byte[] ChatFrame(string message, int groupId = 0, string? data = null)
    {
        var body = ChatlineBody(message, groupId, chatType: 0, data: data);
        var envelope = new List<byte>();
        WriteTag(envelope, ChatlineEnvelopeField, 2);
        WriteVarint(envelope, (ulong)body.Length);
        envelope.AddRange(body);
        return Frame(envelope.ToArray());
    }

    [Fact]
    public void ExtractsMessageAndGroupId()
    {
        Assert.True(ChatlineParser.TryExtract(ChatFrame("hello world", groupId: 12), out var message, out int groupId));

        Assert.Equal("hello world", message);
        Assert.Equal(12, groupId);
    }

    [Fact]
    public void KeepsNonAsciiIntact()
    {
        Assert.True(ChatlineParser.TryExtract(ChatFrame("héllo wörld æøå ✓"), out var message, out _));

        Assert.Equal("héllo wörld æøå ✓", message);
    }

    [Fact]
    public void IgnoresTrailingFieldsItDoesNotCareAbout()
    {
        // Data (field 4) carries mod payloads; the parser must skip past it, not choke.
        Assert.True(ChatlineParser.TryExtract(ChatFrame("hi", data: "{\"some\":\"json\"}"), out var message, out _));

        Assert.Equal("hi", message);
    }

    [Fact]
    public void ParsesAFlattenedPayload()
    {
        // Fork tolerance, same fallback as IdentificationParser: body without the envelope.
        var frame = Frame(ChatlineBody("flattened", groupId: 3));

        Assert.True(ChatlineParser.TryExtract(frame, out var message, out int groupId));
        Assert.Equal("flattened", message);
        Assert.Equal(3, groupId);
    }

    [Fact]
    public void RejectsAnEmptyMessage()
    {
        // An empty line is not worth an event, and is what a mis-parse looks like.
        Assert.False(ChatlineParser.TryExtract(ChatFrame(""), out _, out _));
    }

    [Fact]
    public void RejectsACompressedFrame()
    {
        var body = ChatlineBody("compressed");
        var envelope = new List<byte>();
        WriteTag(envelope, ChatlineEnvelopeField, 2);
        WriteVarint(envelope, (ulong)body.Length);
        envelope.AddRange(body);

        Assert.False(ChatlineParser.TryExtract(Frame(envelope.ToArray(), compressed: true), out _, out _));
    }

    [Fact]
    public void RejectsTruncatedInput()
    {
        var full = ChatFrame("a message long enough to cut");

        Assert.False(ChatlineParser.TryExtract(full.AsSpan(0, 6), out _, out _));
        Assert.False(ChatlineParser.TryExtract(full.AsSpan(0, full.Length - 4), out _, out _));
        Assert.False(ChatlineParser.TryExtract(Array.Empty<byte>(), out _, out _));
    }

    [Fact]
    public void RejectsAFrameWithNoChatlineInIt()
    {
        // Envelope carrying some other field entirely.
        var envelope = new List<byte>();
        WriteTag(envelope, 18, 2);
        WriteVarint(envelope, 3);
        envelope.AddRange(new byte[] { 1, 2, 3 });

        Assert.False(ChatlineParser.TryExtract(Frame(envelope.ToArray()), out _, out _));
    }
}
