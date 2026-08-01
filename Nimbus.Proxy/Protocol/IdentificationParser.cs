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
        if (!VsWire.TryParseHeader(rawFrame, out bool compressed, out int payloadLen)) return false;
        if (compressed) return false; // Identification frames are never compressed.
        if (payloadLen <= 0 || 4 + payloadLen > rawFrame.Length) return false;

        var payload = rawFrame.Slice(4, payloadLen);

        // Preferred path: outer envelope field 2 contains Packet_ClientIdentification.
        // Some forks/dev builds flatten this once, so fall back to parsing the payload
        // directly as an Identification body.
        if (VsWire.TryFindNestedField(payload, fieldNumber: 2, out var ident) && ParseIdentBody(ident, out playerUid, out playerName))
            return true;

        return ParseIdentBody(payload, out playerUid, out playerName);
    }

    private static bool ParseIdentBody(ReadOnlySpan<byte> body, out string playerUid, out string playerName)
    {
        playerUid = "";
        playerName = "";
        int pos = 0;
        while (pos < body.Length)
        {
            if (!VsWire.TryReadVarint(body, ref pos, out ulong key)) return false;
            int fieldNum = (int)(key >> 3);
            int wireType = (int)(key & 0x7);
            if (wireType == 2 && (fieldNum == 2 || fieldNum == 6))
            {
                if (!VsWire.TryReadVarint(body, ref pos, out ulong len)) return false;
                if (pos + (int)len > body.Length) return false;
                string val = Encoding.UTF8.GetString(body.Slice(pos, (int)len));
                pos += (int)len;
                if (fieldNum == 2) playerName = val;
                else playerUid = val;
                if (playerName.Length > 0 && playerUid.Length > 0) return true;
            }
            else
            {
                if (!VsWire.SkipField(body, ref pos, wireType)) return false;
            }
        }
        return playerUid.Length > 0; // PlayerUID is required, name is best-effort.
    }

}
