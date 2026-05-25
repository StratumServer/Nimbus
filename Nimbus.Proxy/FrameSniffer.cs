namespace Nimbus.Proxy;

// Passive VS-TCP frame sniffer. Bytes still flow through immediately, this only observes them.
// One instance per direction (client->server and server->client).
//
// VS frame:
//   [4 bytes BE length+flag] [payload]
//     bit31 of length = compressed flag
//     bits 30..0      = payload length
//   Payload is a protobuf-encoded packet. We don't decode it, just report size, compressed
//   flag, and the first few payload bytes.
//
// A single socket read can contain partial frames, multiple frames, or both. We buffer
// leftovers across calls.
internal sealed class FrameSniffer
{
    private readonly string label;
    private readonly long sessionId;
    private readonly bool clientToServer;
    private readonly SessionState? state;
    private byte[] buf = new byte[16 * 1024];
    private int head;
    private long frameCount;
    private long totalBytes;

    private const int MaxFrameSize = 256 * 1024 * 1024; // 256 MB cap (VS uses 128MB MaxPacketSize)

    // Optional raw frame sink (includes the 4-byte header). Used to capture the client's
    // Identification frame so it can be replayed against a different backend during a swap.
    public Action<string, ReadOnlyMemory<byte>>? OnRawFrame { get; set; }

    // If true, log a line per frame. When false, frames are still parsed silently so
    // OnRawFrame and SessionState still fire.
    public bool Verbose { get; set; }

    public FrameSniffer(long sessionId, string label, SessionState? state = null)
    {
        this.sessionId = sessionId;
        this.label = label;
        this.clientToServer = label.StartsWith("c->", StringComparison.Ordinal);
        this.state = state;
    }

    public void OnBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty) return;
        totalBytes += bytes.Length;
        AppendToBuf(bytes);
        DrainFrames();
    }

    private void AppendToBuf(ReadOnlySpan<byte> src)
    {
        if (head + src.Length > buf.Length)
        {
            int needed = head + src.Length;
            int newSize = buf.Length;
            while (newSize < needed) newSize *= 2;
            if (newSize > MaxFrameSize + 64) newSize = MaxFrameSize + 64;
            var grown = new byte[newSize];
            Buffer.BlockCopy(buf, 0, grown, 0, head);
            buf = grown;
        }
        src.CopyTo(buf.AsSpan(head));
        head += src.Length;
    }

    private void DrainFrames()
    {
        while (head >= 4)
        {
            int header = (buf[0] << 24) | (buf[1] << 16) | (buf[2] << 8) | buf[3];
            bool compressed = (header & unchecked((int)0x80000000)) != 0;
            int payloadLen = header & 0x7FFFFFFF;

            if (payloadLen == 0)
            {
                Shift(4);
                continue;
            }
            if (payloadLen > MaxFrameSize)
            {
                Log.Warn($"[s{sessionId} {label}] frame too large ({payloadLen} bytes), sniffer giving up");
                head = 0;
                return;
            }
            if (head < 4 + payloadLen) return; // wait for more bytes

            frameCount++;
            string name;
            if (compressed)
            {
                // Compressed payload uses zlib (per VS TcpNetConnection). We don't inflate here
                // because the sniffer is best-effort and most handshake-relevant packets are uncompressed.
                name = "<zlib>";
            }
            else
            {
                name = PacketDispatch.Describe(clientToServer, new ReadOnlySpan<byte>(buf, 4, payloadLen));
                state?.OnFrame(clientToServer, name);
            }

            // Only spam Trace for chatty packets; Info for handshake-shaped ones so they're easy to spot.
            bool interesting = !compressed && IsHandshakePacket(name);
            if (Verbose || interesting)
            {
                string preview = HexPreview(buf, 4, payloadLen, 12);
                string msg = $"[s{sessionId} {label}] frame #{frameCount} {name} len={payloadLen} comp={(compressed ? 1 : 0)} bytes={preview}";
                if (interesting) Log.Info(msg); else Log.Trace(msg);
            }

            // Hand the raw frame to any listener. They get a copy so we can recycle buf.
            if (OnRawFrame != null)
            {
                var copy = new byte[4 + payloadLen];
                Buffer.BlockCopy(buf, 0, copy, 0, 4 + payloadLen);
                try { OnRawFrame(name, copy); } catch (Exception ex) { Log.Warn($"[s{sessionId} {label}] OnRawFrame threw: {ex.Message}"); }
            }

            Shift(4 + payloadLen);
        }
    }

    private void Shift(int n)
    {
        int remaining = head - n;
        if (remaining > 0) Buffer.BlockCopy(buf, n, buf, 0, remaining);
        head = remaining;
    }

    // Peek at the first byte of payload to get the protobuf field header.
    // VS packets use field 1 (packet id discriminator), encoded as (1 << 3) | wireType.
    private static int TryPeekProtobufFieldHeader(byte[] buf, int offset, int len)
    {
        if (len <= 0) return 0;
        return buf[offset];
    }

    private static bool IsHandshakePacket(string name)
    {
        if (name is "Identification" or "LevelInitialize" or "LevelFinalize" or "ServerReady"
                 or "Redirect" or "DisconnectPlayer" or "RequestJoin" or "Leave"
                 or "LoginTokenQuery" or "TokenAnswer" or "WorldMetaData" or "NetworkChannels")
            return true;
        // Bare-Id discriminator: only the handshake-shaped values are interesting (skip Ping/PingReply spam).
        return name is "Id=1(PlayerIdentification)" or "Id=11(RequestJoin)" or "Id=14(Leave)"
                    or "Id=26(ClientLoaded)" or "Id=29(ClientPlaying)" or "Id=33(LoginTokenQuery)"
                    or "Id=1(ServerIdentification)" or "Id=9(DisconnectPlayer)" or "Id=29(ServerRedirect)"
                    or "Id=73(ServerReady)" or "Id=77(TokenAnswer)";
    }

    private static string HexPreview(byte[] buf, int offset, int len, int max)
    {
        int n = Math.Min(len, max);
        var sb = new System.Text.StringBuilder(n * 3);
        for (int i = 0; i < n; i++) sb.Append(buf[offset + i].ToString("X2")).Append(' ');
        if (len > n) sb.Append("...");
        return sb.ToString().TrimEnd();
    }

    public string Stats() => $"frames={frameCount} totalBytes={totalBytes}";
}
