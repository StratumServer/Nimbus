using System.Net;

namespace Nimbus.Proxy;

// Bounded state for UDP sockets and their creation. A lease remains
// held until the UDP socket is disposed.
internal sealed class UdpAdmission
{
    private readonly object gate = new();
    private readonly AdvancedConfig cfg;
    private readonly TimeProvider clock;
    private readonly Dictionary<IPAddress, AddressState> addresses = new();
    private DateTimeOffset window;
    private int attempts, pending;

    public UdpAdmission(AdvancedConfig cfg, TimeProvider? clock = null)
    {
        this.cfg = cfg;
        this.clock = clock ?? TimeProvider.System;
        window = this.clock.GetUtcNow();
    }

    public IDisposable? TryAcquire(IPAddress address)
    {
        address = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        lock (gate)
        {
            DateTimeOffset now = clock.GetUtcNow();
            if (now - window >= TimeSpan.FromMinutes(1)) { window = now; attempts = 0; }
            if (attempts >= cfg.MaxUdpSessionCreationsPerMinute) return null;
            attempts++;
            AddressState? state = GetOrCreateAddress(address, now);
            if (state == null) return null;
            if (now - state.Window >= TimeSpan.FromMinutes(1)) { state.Window = now; state.Attempts = 0; }
            if (state.Attempts >= cfg.MaxUdpSessionCreationsPerAddressPerMinute) return null;
            state.Attempts++;
            if (pending >= cfg.MaxUdpSessions || state.Pending >= cfg.MaxUdpSessionsPerAddress) return null;
            pending++;
            state.Pending++;
            return new Lease(this, state);
        }
    }

    private AddressState? GetOrCreateAddress(IPAddress address, DateTimeOffset now)
    {
        if (addresses.TryGetValue(address, out AddressState? state)) return state;
        if (addresses.Count >= cfg.MaxUdpTrackedAddresses)
        {
            foreach (var entry in addresses.ToArray())
                if (entry.Value.Pending == 0 && now - entry.Value.Window >= TimeSpan.FromMinutes(1))
                    addresses.Remove(entry.Key);
            if (addresses.Count >= cfg.MaxUdpTrackedAddresses) return null;
        }
        state = new AddressState { Window = now };
        addresses.Add(address, state);
        return state;
    }

    private void Release(AddressState state)
    {
        lock (gate) { pending--; state.Pending--; }
    }

    private sealed class AddressState
    {
        public DateTimeOffset Window;
        public int Attempts, Pending;
    }

    private sealed class Lease(UdpAdmission owner, AddressState state) : IDisposable
    {
        private UdpAdmission? owner = owner;
        public void Dispose() => Interlocked.Exchange(ref owner, null)?.Release(state);
    }
}
