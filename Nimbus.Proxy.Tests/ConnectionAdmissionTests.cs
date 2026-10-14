using System.Net;
using Xunit;

namespace Nimbus.Proxy.Tests;

public class ConnectionAdmissionTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void CapacityIsGlobalAndPerAddress_AndLeaseReleaseIsIdempotent()
    {
        var cfg = new AdvancedConfig { MaxPendingConnections = 2, MaxPendingConnectionsPerAddress = 1 };
        var gate = new ConnectionAdmission(cfg);
        IDisposable first = Assert.IsAssignableFrom<IDisposable>(gate.TryAcquire(IPAddress.Parse("192.0.2.1")));
        Assert.Null(gate.TryAcquire(IPAddress.Parse("::ffff:192.0.2.1")));
        using IDisposable second = Assert.IsAssignableFrom<IDisposable>(gate.TryAcquire(IPAddress.Parse("192.0.2.2")));
        Assert.Null(gate.TryAcquire(IPAddress.Parse("192.0.2.3")));
        first.Dispose(); first.Dispose();
        using IDisposable replacement = Assert.IsAssignableFrom<IDisposable>(gate.TryAcquire(IPAddress.Parse("192.0.2.3")));
        Assert.Null(gate.TryAcquire(IPAddress.Parse("192.0.2.4")));
    }

    [Fact]
    public void CreationRateSurvivesDisconnect_AndResetsWithoutSleeping()
    {
        var clock = new Clock();
        var cfg = new AdvancedConfig { MaxConnectionsPerMinute = 3, MaxConnectionsPerAddressPerMinute = 1 };
        var gate = new ConnectionAdmission(cfg, clock);
        gate.TryAcquire(IPAddress.Parse("192.0.2.1"))!.Dispose();
        Assert.Null(gate.TryAcquire(IPAddress.Parse("192.0.2.1")));
        gate.TryAcquire(IPAddress.Parse("192.0.2.2"))!.Dispose();
        Assert.Null(gate.TryAcquire(IPAddress.Parse("192.0.2.3")));
        clock.Now += TimeSpan.FromMinutes(1);
        gate.TryAcquire(IPAddress.Parse("192.0.2.1"))!.Dispose();
    }

    [Fact]
    public void AddressChurnCannotGrowTheTrackingTable()
    {
        var clock = new Clock();
        var gate = new ConnectionAdmission(new AdvancedConfig { MaxTrackedAddresses = 2 }, clock);
        gate.TryAcquire(IPAddress.Parse("192.0.2.1"))!.Dispose();
        gate.TryAcquire(IPAddress.Parse("192.0.2.2"))!.Dispose();
        Assert.Null(gate.TryAcquire(IPAddress.Parse("192.0.2.3")));
        clock.Now += TimeSpan.FromMinutes(1);
        gate.TryAcquire(IPAddress.Parse("192.0.2.3"))!.Dispose();
    }
}
