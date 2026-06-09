using System.Text;

namespace Nimbus.Proxy;

// Extracts the PlayerUID (and Playername) from a captured client Identification frame so the
// proxy can mint a pre-swap reservation against the registry.
//
// Frame layout: VS TCP header (4 BE bytes) then a Packet_Client envelope. Inside that,
// field 2 (wire-type 2) holds the nested Packet_ClientIdentification. Relevant string fields:
//   2 -> Playername     6 -> PlayerUID
internal static class IdentificationParser
{
    // Parse player UID + name out of a captured raw client-to-server frame (length-prefixed,
    // the way it lives in ProxySession.capturedIdentification). Returns false on malformed input.
    public static bool TryExtract(ReadOnlySpan<byte> rawFrame, out string playerUid, out string playerName)
    {
        playerUid = "";
        playerName = "";
        if (rawFrame.Length < 5) return false;

        // VS TCP header: 4 bytes BE, bit 31 = compressed flag, bits 30..0 = payload length.
        uint header = (uint)((rawFrame[0] << 24) | (rawFrame[1] << 16) | (rawFrame[2] << 8) | rawFrame[3]);
        bool compressed = (header & 0x80000000u) != 0;
        int payloadLen = (int)(header & 0x7FFFFFFFu);
        if (compressed) return false; // Identification frames are never compressed.
        if (payloadLen <= 0 || 4 + payloadLen > rawFrame.Length) return false;

        var payload = rawFrame.Slice(4, payloadLen);

        // Preferred path: outer envelope field 2 contains Packet_ClientIdentification.
        // Some forks/dev builds flatten this once, so fall back to parsing the payload
        // directly as an Identification body.
        if (FindNestedField2(payload, out var ident) && ParseIdentBody(ident, out playerUid, out playerName))
            return true;

        return ParseIdentBody(payload, out playerUid, out playerName);
    }

    private static bool FindNestedField2(ReadOnlySpan<byte> body, out ReadOnlySpan<byte> nested)
    {
        nested = default;
        int pos = 0;
        while (pos < body.Length)
        {
            if (!TryReadVarint(body, ref pos, out ulong key)) return false;
            int fieldNum = (int)(key >> 3);
            int wireType = (int)(key & 0x7);
            if (fieldNum == 2 && wireType == 2)
            {
                if (!TryReadVarint(body, ref pos, out ulong len)) return false;
                if (pos + (int)len > body.Length) return false;
                nested = body.Slice(pos, (int)len);
                return true;
            }
            if (!SkipField(body, ref pos, wireType)) return false;
        }
        return false;
    }

    private static bool ParseIdentBody(ReadOnlySpan<byte> body, out string playerUid, out string playerName)
    {
        playerUid = "";
        playerName = "";
        int pos = 0;
        while (pos < body.Length)
        {
            if (!TryReadVarint(body, ref pos, out ulong key)) return false;
            int fieldNum = (int)(key >> 3);
            int wireType = (int)(key & 0x7);
            if (wireType == 2 && (fieldNum == 2 || fieldNum == 6))
            {
                if (!TryReadVarint(body, ref pos, out ulong len)) return false;
                if (pos + (int)len > body.Length) return false;
                string val = Encoding.UTF8.GetString(body.Slice(pos, (int)len));
                pos += (int)len;
                if (fieldNum == 2) playerName = val;
                else playerUid = val;
                if (playerName.Length > 0 && playerUid.Length > 0) return true;
            }
            else
            {
                if (!SkipField(body, ref pos, wireType)) return false;
            }
        }
        return playerUid.Length > 0; // PlayerUID is required, name is best-effort.
    }

    private static bool SkipField(ReadOnlySpan<byte> buf, ref int pos, int wireType)
    {
        switch (wireType)
        {
            case 0: // varint
                return TryReadVarint(buf, ref pos, out _);
            case 1: // 64-bit
                if (pos + 8 > buf.Length) return false;
                pos += 8;
                return true;
            case 2: // length-delim
                if (!TryReadVarint(buf, ref pos, out ulong len)) return false;
                if (pos + (int)len > buf.Length) return false;
                pos += (int)len;
                return true;
            case 5: // 32-bit
                if (pos + 4 > buf.Length) return false;
                pos += 4;
                return true;
            default:
                return false; // groups (3,4) and unknown wire types
        }
    }

    private static bool TryReadVarint(ReadOnlySpan<byte> buf, ref int pos, out ulong value)
    {
        value = 0;
        int shift = 0;
        while (pos < buf.Length)
        {
            byte b = buf[pos++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return true;
            shift += 7;
            if (shift > 63) return false;
        }
        return false;
    }
}
