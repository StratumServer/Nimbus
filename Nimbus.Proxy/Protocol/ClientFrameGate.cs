using System.IO.Compression;

namespace Nimbus.Proxy;

// Hold complete frames before authorization. Socket-read boundaries must not let a
// fragmented Identification reach the backend before the whitelist check.
internal sealed class ClientFrameGate
{
    private readonly byte[] header = new byte[4];
    private int headerBytes;
    private byte[]? frame;
    private int frameBytes;
    private bool identified;

    public async ValueTask ForwardAsync(ReadOnlyMemory<byte> bytes,
        Func<ReadOnlyMemory<byte>, bool, ValueTask> forward)
    {
        while (!bytes.IsEmpty)
        {
            if (frame == null)
            {
                int take = Math.Min(4 - headerBytes, bytes.Length);
                bytes.Span[..take].CopyTo(header.AsSpan(headerBytes));
                headerBytes += take;
                bytes = bytes[take..];
                if (headerBytes < 4) return;
                _ = VsWire.TryParseHeader(header, out _, out int length);
                int limit = identified ? VsWire.MaxFrameSize : ClientSessionRunner.MaxFirstFrameSize;
                if (length > limit) throw new InvalidDataException("Client frame exceeds the admission limit.");
                frame = new byte[4 + length];
                header.CopyTo(frame, 0);
                frameBytes = 4;
            }
            int copied = Math.Min(frame.Length - frameBytes, bytes.Length);
            bytes.Span[..copied].CopyTo(frame.AsSpan(frameBytes));
            frameBytes += copied;
            bytes = bytes[copied..];
            if (frameBytes != frame.Length) return;

            byte[] complete = frame;
            frame = null;
            headerBytes = 0;
            bool identification = Inspect(complete);
            if (identification)
            {
                if (identified) throw new InvalidDataException("Repeated Identification is not permitted.");
                identified = true;
            }
            await forward(complete, identification).ConfigureAwait(false);
        }
    }

    private bool Inspect(byte[] raw)
    {
        _ = VsWire.TryParseHeader(raw, out bool compressed, out _);
        byte[]? inflated = null;
        if (compressed)
        {
            using var input = new MemoryStream(raw, 4, raw.Length - 4);
            using var zlib = new ZLibStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            byte[] buffer = new byte[4096];
            int limit = identified ? VsWire.MaxFrameSize : ClientSessionRunner.MaxFirstFrameSize;
            int read;
            while ((read = zlib.Read(buffer)) > 0)
            {
                if (read > limit - output.Length) throw new InvalidDataException("Inflated client frame exceeds the admission limit.");
                output.Write(buffer, 0, read);
            }
            inflated = output.ToArray();
        }
        ReadOnlySpan<byte> payload = inflated == null ? raw.AsSpan(4) : inflated;
        if (!IdentificationParser.TryInspectPayload(payload, out bool identification, out _, out _)
            || (compressed && identification))
            throw new InvalidDataException("Malformed or compressed Identification envelope.");
        return identification;
    }
}
