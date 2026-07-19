namespace Nimbus.Proxy;

internal static class QueryAnswerBuilder
{
    public static byte[] BuildFrame(ServerQueryStatus status)
    {
        var body = new MemoryStream();
        WriteStringIfSet(body, 1, status.Name);
        WriteStringIfSet(body, 2, status.Motd);
        WriteIntIfNonZero(body, 3, status.PlayerCount);
        WriteIntIfNonZero(body, 4, status.MaxPlayers);
        WriteStringIfSet(body, 5, status.GameMode);
        if (status.Password)
            VsWire.WriteVarintField(body, 6, 1);
        WriteStringIfSet(body, 7, status.ServerVersion);

        // Packet_Server envelope: Id (field 90) = 28, QueryAnswer body under field 28.
        return VsWire.BuildServerPacketFrame(packetId: 28, bodyField: 28, body.ToArray());
    }

    public static bool IsQueryFrame(ReadOnlyMemory<byte> frame)
    {
        var bytes = frame.Span;
        if (!VsWire.TryParseHeader(bytes, out bool compressed, out int len)) return false;
        if (compressed) return false;
        if (len != bytes.Length - 4) return false;
        var payload = bytes.Slice(4, len);
        return IsBareQueryId(payload) || IsQueryField(payload);
    }

    private static bool IsBareQueryId(ReadOnlySpan<byte> payload)
        => payload.Length == 2 && payload[0] == 8 && payload[1] == 15;

    private static bool IsQueryField(ReadOnlySpan<byte> payload)
        => payload.Length == 2 && payload[0] == 82 && payload[1] == 0;

    // proto3-style presence: zero/empty fields are omitted entirely.
    private static void WriteStringIfSet(Stream s, int fieldNumber, string? value)
    {
        if (!string.IsNullOrEmpty(value))
            VsWire.WriteStringField(s, fieldNumber, value);
    }

    private static void WriteIntIfNonZero(Stream s, int fieldNumber, int value)
    {
        if (value != 0)
            VsWire.WriteVarintField(s, fieldNumber, (ulong)(uint)value);
    }
}

internal sealed record ServerQueryStatus(
    string Name,
    string Motd,
    int PlayerCount,
    int MaxPlayers,
    string GameMode,
    bool Password,
    string ServerVersion);
