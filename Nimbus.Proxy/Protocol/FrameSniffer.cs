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
// leftovers across calls. The buffer is drained with a moving read offset: consuming a
// frame just advances `start`, and the remainder is compacted to the front at most once
// per OnBytes call (when appending needs the room), instead of memmoving the tail after
// every frame. This is the hot path: every byte of every session flows through here in
// both directions.
internal sealed class FrameSniffer
{
    private readonly string label;
    private readonly long sessionId;
    private readonly bool clientToServer;
    private readonly SessionState? state;
    private byte[] buf = new byte[16 * 1024];
    private int start;   // read offset: first unconsumed byte
    private int head;    // write offset: one past the last buffered byte
    private long frameCount;
    private long totalBytes;

    private const int MaxFrameSize = VsWire.MaxFrameSize;

    private int Buffered => head - start;

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
            // Reclaim the consumed prefix first; grow only if the live bytes plus the
            // incoming ones genuinely don't fit.
            if (start > 0)
            {
                Buffer.BlockCopy(buf, start, buf, 0, Buffered);
                head -= start;
                start = 0;
            }
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
        }
        src.CopyTo(buf.AsSpan(head));
        head += src.Length;
    }

    private void DrainFrames()
    {
        while (Buffered >= 4)
        {
            VsWire.TryParseHeader(buf.AsSpan(start, 4), out bool compressed, out int payloadLen);

            if (payloadLen == 0)
            {
                start += 4;
                continue;
            }
            if (payloadLen > MaxFrameSize)
            {
                Log.Warn($"[s{sessionId} {label}] frame too large ({payloadLen} bytes), sniffer giving up");
                start = 0;
                head = 0;
                return;
            }
            if (Buffered < 4 + payloadLen) break; // wait for more bytes

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
                name = PacketDispatch.Describe(clientToServer, new ReadOnlySpan<byte>(buf, start + 4, payloadLen));
                state?.OnFrame(clientToServer, name);
            }

            // Trace chatty packets. Keep handshake-shaped packets visible.
            bool interesting = !compressed && IsHandshakePacket(name);
            if (Verbose || interesting)
            {
                string preview = HexPreview(buf, start + 4, payloadLen, 12);
                string msg = $"[s{sessionId} {label}] frame #{frameCount} {name} len={payloadLen} comp={(compressed ? 1 : 0)} bytes={preview}";
                if (interesting) Log.Info(msg); else Log.Trace(msg);
            }

            // Hand the raw frame to any listener. They get a copy so we can recycle buf.
            if (OnRawFrame != null)
            {
                var copy = new byte[4 + payloadLen];
                Buffer.BlockCopy(buf, start, copy, 0, 4 + payloadLen);
                try { OnRawFrame(name, copy); } catch (Exception ex) { Log.Warn($"[s{sessionId} {label}] OnRawFrame threw: {ex.Message}"); }
            }

            start += 4 + payloadLen;
        }

        if (start == head)
        {
            // Fully drained: reset so the next read starts at the front without a compact.
            start = 0;
            head = 0;
        }
    }

    private static bool IsHandshakePacket(string name)
    {
        if (name is "Identification" or "LevelInitialize" or "LevelFinalize" or "ServerReady"
                 or "Redirect" or "DisconnectPlayer" or "RequestJoin" or "Leave"
                 or "LoginTokenQuery" or "TokenAnswer" or "WorldMetaData" or "NetworkChannels")
            return true;
        // Ignore Ping noise on the bare-Id path.
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
