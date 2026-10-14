using System.Text;

namespace Nimbus.Proxy;

internal static class IdentificationParser
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static bool TryExtract(ReadOnlySpan<byte> rawFrame, out string playerUid, out string playerName)
    {
        playerUid = playerName = "";
        return VsWire.TryParseHeader(rawFrame, out bool compressed, out int length)
            && !compressed && length == rawFrame.Length - 4
            && TryInspectPayload(rawFrame[4..], out bool identification, out playerUid, out playerName)
            && identification;
    }

    // Inspect the complete envelope: Identification need not be the first field.
    public static bool TryInspectPayload(ReadOnlySpan<byte> payload, out bool identification,
        out string playerUid, out string playerName)
    {
        identification = false;
        playerUid = playerName = "";
        int pos = 0;
        ulong? packetId = null;
        while (pos < payload.Length)
        {
            if (!VsWire.TryReadVarint(payload, ref pos, out ulong key) || key >> 3 is 0 or > 536870911) return false;
            int field = (int)(key >> 3);
            int wire = (int)(key & 7);
            if (field == 1)
            {
                if (packetId != null || wire != 0 || !VsWire.TryReadVarint(payload, ref pos, out ulong id)) return false;
                packetId = id;
            }
            else if (field == 2)
            {
                if (identification || wire != 2 || !VsWire.TryReadLength(payload, ref pos, out int len)) return false;
                identification = true;
                if (!ParseBody(payload.Slice(pos, len), out playerUid, out playerName)) return false;
                pos += len;
            }
            else if (!VsWire.SkipField(payload, ref pos, wire)) return false;
        }
        return identification ? packetId is null or 1 : packetId != 1;
    }

    private static bool ParseBody(ReadOnlySpan<byte> body, out string uid, out string name)
    {
        uid = name = "";
        bool seenName = false, seenUid = false;
        int pos = 0;
        while (pos < body.Length)
        {
            if (!VsWire.TryReadVarint(body, ref pos, out ulong key) || key >> 3 is 0 or > 536870911) return false;
            int field = (int)(key >> 3);
            int wire = (int)(key & 7);
            // Vintage Story 1.22.x: Playername = 2, PlayerUID = 6 (wire tag 50).
            if (field is 2 or 6)
            {
                if (wire != 2 || (field == 2 ? seenName : seenUid)
                    || !VsWire.TryReadLength(body, ref pos, out int len)) return false;
                string value;
                try { value = StrictUtf8.GetString(body.Slice(pos, len)); }
                catch (DecoderFallbackException) { return false; }
                pos += len;
                if (field == 2) { seenName = true; name = value; }
                else { seenUid = true; uid = value; }
            }
            else if (!VsWire.SkipField(body, ref pos, wire)) return false;
        }
        return seenUid && uid.Length > 0;
    }
}
