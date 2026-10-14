using System.Net;
using System.Text;

namespace Nimbus.Proxy;

// Tokens are learned on TCP and published only after the backend acknowledges login.
// One authenticated TCP session may claim one UDP endpoint, including behind a NAT.
internal sealed class UdpAuthorizationTable
{
    private readonly object gate = new();
    private readonly Dictionary<string, Authorization> tokens = new(StringComparer.Ordinal);
    private readonly Dictionary<long, string> owners = new();
    private readonly int capacity;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public UdpAuthorizationTable(int capacity = 4096) => this.capacity = capacity;

    public void Publish(long owner, IPAddress address, string token, BackendEndpoint target)
    {
        if (string.IsNullOrEmpty(token)) return;
        lock (gate)
        {
            if (owners.TryGetValue(owner, out string? existing) && existing == token)
            {
                tokens[token].Target = target;
                return;
            }
            RemoveLocked(owner);
            if (tokens.Count >= capacity || tokens.ContainsKey(token)) return;
            tokens.Add(token, new Authorization(address, target));
            owners.Add(owner, token);
        }
    }

    public Authorization? Claim(IPEndPoint endpoint, ReadOnlySpan<byte> packet)
    {
        if (!TryReadToken(packet, out string token)) return null;
        lock (gate)
        {
            if (!tokens.TryGetValue(token, out Authorization? authorization)
                || !authorization.Address.Equals(endpoint.Address)) return null;
            if (authorization.Endpoint != null && !authorization.Endpoint.Equals(endpoint)) return null;
            authorization.Endpoint = endpoint;
            return authorization;
        }
    }

    public void Update(long owner, BackendEndpoint target)
    {
        lock (gate)
            if (owners.TryGetValue(owner, out string? token)) tokens[token].Target = target;
    }

    public void Remove(long owner) { lock (gate) RemoveLocked(owner); }
    private void RemoveLocked(long owner)
    {
        if (owners.Remove(owner, out string? token) && tokens.Remove(token, out Authorization? authorization))
            authorization.Revoke();
    }

    // Packet_UdpPacket.Id = 1, ConnectionPacket = field 5; its LoginToken = field 1.
    // Validate the complete packet, so duplicate fields cannot select different tokens.
    internal static bool TryReadToken(ReadOnlySpan<byte> packet, out string token)
    {
        token = "";
        if (packet.Length > 5000) return false;
        int pos = 0;
        bool seenId = false, seenConnection = false;
        while (pos < packet.Length)
        {
            if (!VsWire.TryReadVarint(packet, ref pos, out ulong key) || key >> 3 is 0 or > 536870911) return false;
            int field = (int)(key >> 3), wire = (int)(key & 7);
            bool valid = field switch
            {
                1 => ReadPacketId(packet, ref pos, wire, ref seenId),
                5 => ReadConnection(packet, ref pos, wire, ref seenConnection, out token),
                _ => VsWire.SkipField(packet, ref pos, wire),
            };
            if (!valid) return false;
        }
        return seenId && seenConnection && token.Length > 0;
    }

    private static bool ReadPacketId(ReadOnlySpan<byte> packet, ref int pos, int wire, ref bool seen)
    {
        if (seen || wire != 0 || !VsWire.TryReadVarint(packet, ref pos, out ulong id) || id != 1) return false;
        seen = true;
        return true;
    }

    private static bool ReadConnection(ReadOnlySpan<byte> packet, ref int pos, int wire, ref bool seen, out string token)
    {
        token = "";
        if (seen || wire != 2 || !VsWire.TryReadLength(packet, ref pos, out int length)) return false;
        seen = true;
        if (!ReadTokenBody(packet.Slice(pos, length), out token)) return false;
        pos += length;
        return true;
    }

    private static bool ReadTokenBody(ReadOnlySpan<byte> body, out string token)
    {
        token = "";
        bool seen = false;
        int pos = 0;
        while (pos < body.Length)
        {
            if (!VsWire.TryReadVarint(body, ref pos, out ulong key) || key >> 3 is 0 or > 536870911) return false;
            int field = (int)(key >> 3), wire = (int)(key & 7);
            if (field == 1)
            {
                if (seen || wire != 2 || !VsWire.TryReadLength(body, ref pos, out int length)) return false;
                if (!DecodeToken(body.Slice(pos, length), out token)) return false;
                pos += length;
                seen = true;
            }
            else if (!VsWire.SkipField(body, ref pos, wire)) return false;
        }
        return seen && token.Length > 0;
    }

    private static bool DecodeToken(ReadOnlySpan<byte> bytes, out string token)
    {
        token = "";
        try { token = StrictUtf8.GetString(bytes); return true; }
        catch (DecoderFallbackException) { return false; }
    }

    internal sealed class Authorization : IDisposable
    {
        private readonly CancellationTokenSource revoked = new();
        private int disposed;
        public Authorization(IPAddress address, BackendEndpoint target)
        {
            Address = address;
            Target = target;
            Revoked = revoked.Token;
        }
        public IPAddress Address { get; }
        public IPEndPoint? Endpoint { get; set; }
        public BackendEndpoint Target;
        public CancellationToken Revoked { get; }
        public void Revoke() => Dispose();
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            try { revoked.Cancel(); }
            finally { revoked.Dispose(); }
        }
    }
}
