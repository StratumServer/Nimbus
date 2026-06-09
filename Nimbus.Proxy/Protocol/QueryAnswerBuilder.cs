using System.Text;

namespace Nimbus.Proxy;

internal static class QueryAnswerBuilder
{
    public static byte[] BuildFrame(ServerQueryStatus status)
    {
        var body = new MemoryStream();
        WriteStringField(body, 1, status.Name);
        WriteStringField(body, 2, status.Motd);
        WriteIntField(body, 3, status.PlayerCount);
        WriteIntField(body, 4, status.MaxPlayers);
        WriteStringField(body, 5, status.GameMode);
        if (status.Password)
        {
            WriteTag(body, 6, 0);
            WriteVarint(body, 1);
        }
        WriteStringField(body, 7, status.ServerVersion);
        byte[] bodyBytes = body.ToArray();

        var env = new MemoryStream();
        WriteTag(env, 90, 0);
        WriteVarint(env, 28);
        WriteTag(env, 28, 2);
        WriteVarint(env, bodyBytes.Length);
        env.Write(bodyBytes, 0, bodyBytes.Length);
        return WrapFrame(env.ToArray());
    }

    public static bool IsQueryFrame(ReadOnlyMemory<byte> frame)
    {
        var bytes = frame.Span;
        if (bytes.Length < 5) return false;
        int header = (bytes[0] << 24) | (bytes[1] << 16) | (bytes[2] << 8) | bytes[3];
        if ((header & unchecked((int)0x80000000)) != 0) return false;
        int len = header & 0x7FFFFFFF;
        if (len != bytes.Length - 4) return false;
        var payload = bytes.Slice(4, len);
        return IsBareQueryId(payload) || IsQueryField(payload);
    }

    private static bool IsBareQueryId(ReadOnlySpan<byte> payload)
        => payload.Length == 2 && payload[0] == 8 && payload[1] == 15;

    private static bool IsQueryField(ReadOnlySpan<byte> payload)
        => payload.Length == 2 && payload[0] == 82 && payload[1] == 0;

    private static void WriteStringField(Stream s, int fieldNumber, string? value)
    {
        if (string.IsNullOrEmpty(value)) return;
        var bytes = Encoding.UTF8.GetBytes(value);
        WriteTag(s, fieldNumber, 2);
        WriteVarint(s, bytes.Length);
        s.Write(bytes, 0, bytes.Length);
    }

    private static void WriteIntField(Stream s, int fieldNumber, int value)
    {
        if (value == 0) return;
        WriteTag(s, fieldNumber, 0);
        WriteVarint(s, value);
    }

    private static byte[] WrapFrame(byte[] payload)
    {
        int len = payload.Length;
        var frame = new byte[4 + len];
        frame[0] = (byte)((len >> 24) & 0x7F);
        frame[1] = (byte)((len >> 16) & 0xFF);
        frame[2] = (byte)((len >> 8) & 0xFF);
        frame[3] = (byte)(len & 0xFF);
        Buffer.BlockCopy(payload, 0, frame, 4, len);
        return frame;
    }

    private static void WriteTag(Stream s, int fieldNumber, int wireType)
        => WriteVarint(s, (fieldNumber << 3) | wireType);

    private static void WriteVarint(Stream s, int value)
    {
        var v = (uint)value;
        while (v >= 0x80)
        {
            s.WriteByte((byte)(v | 0x80));
            v >>= 7;
        }
        s.WriteByte((byte)v);
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
