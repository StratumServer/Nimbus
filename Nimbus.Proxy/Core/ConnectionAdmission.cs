using System.Net;

namespace Nimbus.Proxy;

// Bounded state for pending TCP handshakes and connection creation. A lease remains
// held until the backend completes login or the connection is closed.
internal sealed class ConnectionAdmission
{
    private readonly object gate = new();
    private readonly AdvancedConfig cfg;
    private readonly TimeProvider clock;
    private readonly Dictionary<IPAddress, AddressState> addresses = new();
    private DateTimeOffset window;
    private int attempts, pending;
    internal int PendingCount { get { lock (gate) return pending; } }

    public ConnectionAdmission(AdvancedConfig cfg, TimeProvider? clock = null)
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
            if (attempts >= cfg.MaxConnectionsPerMinute) return null;
            attempts++;
            if (!addresses.TryGetValue(address, out AddressState? state))
            {
                if (addresses.Count >= cfg.MaxTrackedAddresses)
                {
                    foreach (var entry in addresses.ToArray())
                        if (entry.Value.Pending == 0 && now - entry.Value.Window >= TimeSpan.FromMinutes(1))
                            addresses.Remove(entry.Key);
                    if (addresses.Count >= cfg.MaxTrackedAddresses) return null;
                }
                state = new AddressState { Window = now };
                addresses.Add(address, state);
            }
            if (now - state.Window >= TimeSpan.FromMinutes(1)) { state.Window = now; state.Attempts = 0; }
            if (state.Attempts >= cfg.MaxConnectionsPerAddressPerMinute) return null;
            state.Attempts++;
            if (pending >= cfg.MaxPendingConnections || state.Pending >= cfg.MaxPendingConnectionsPerAddress) return null;
            pending++;
            state.Pending++;
            return new Lease(this, state);
        }
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

    private sealed class Lease(ConnectionAdmission owner, AddressState state) : IDisposable
    {
        private ConnectionAdmission? owner = owner;
        public void Dispose() => Interlocked.Exchange(ref owner, null)?.Release(state);
    }
}
