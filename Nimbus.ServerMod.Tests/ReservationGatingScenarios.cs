using Atlas.Api;
using Atlas.XUnit;
using Xunit;

namespace Nimbus.ServerMod.Tests;

/// <summary>
/// Exercises Nimbus.ServerMod's reservation gating (the inbound half of a transfer) on a
/// real embedded Vintage Story server, against a fake registry on a real loopback port.
/// One embedded server is shared by every scenario in this class; each scenario writes its
/// own config (file + /nimbus reload) and uses its own player name.
/// </summary>
public class ReservationGatingScenarios : AtlasScenarioBase
{
    private const string Secret = "atlas-shared-secret";

    private static object MakeReservation(string realIp) => new
    {
        id = "res-atlas-1",
        playerUid = "",              // the mod matches by its own join UID, not this field
        playerName = "test",
        sourceServerId = "hub",
        targetServerId = "backend-test",
        expiresAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 60,
        reason = "atlas scenario",
        realRemoteIp = realIp,
        realRemotePort = 54321,
    };

    [AtlasScenario]
    public async Task Join_WithValidReservation_IsAdmitted_AndForwardingIsStored()
    {
        using var registry = new FakeRegistry(Secret);
        var nimbus = await NimbusHarness.ConfigureAsync(World, registry.Url, Secret, reservationRequired: true);

        registry.NextReservation = MakeReservation("203.0.113.7");
        ITestPlayer alice = await World.JoinPlayer("alice");
        string uid = alice.Player.PlayerUID;

        // Reservation consumption is async (fire-and-forget off the join event);
        // wait for the forwarding record instead of assuming it is immediate.
        await World.Until(() => nimbus.GetForwardedPlayer(uid) != null);

        Assert.Equal("203.0.113.7", nimbus.ForwardedRealIp(uid));
        Assert.True(alice.IsConnected, "player with a valid reservation must stay connected");
    }

    [AtlasScenario]
    public async Task Join_WithoutReservation_WhenRequired_IsKicked()
    {
        using var registry = new FakeRegistry(Secret); // no reservation scripted
        await NimbusHarness.ConfigureAsync(World, registry.Url, Secret, reservationRequired: true);

        ITestPlayer bob;
        try
        {
            bob = await World.JoinPlayer("bob");
        }
        catch (AtlasSetupException)
        {
            // The kick can land while the join handshake is still in flight; a failed
            // join IS the expected outcome then.
            return;
        }

        await World.Until(() => !bob.IsConnected, timeoutTicks: 300);
    }

    [AtlasScenario]
    public async Task Join_WithoutReservation_WhenNotRequired_IsAdmitted()
    {
        using var registry = new FakeRegistry(Secret);
        var nimbus = await NimbusHarness.ConfigureAsync(World, registry.Url, Secret, reservationRequired: false);

        ITestPlayer carol = await World.JoinPlayer("carol");
        string uid = carol.Player.PlayerUID;

        // Wait until the mod has actually asked the registry, then a little longer.
        await World.Until(() => registry.Requests.Any(r => r.Path.Contains("consume-by-uid")));
        await World.Ticks(20);

        Assert.True(carol.IsConnected, "player must stay connected when reservations are optional");
        Assert.Null(nimbus.GetForwardedPlayer(uid));
    }

    [AtlasScenario]
    public async Task ConsumeRequest_IsHmacSigned_WithTheSharedSecret()
    {
        using var registry = new FakeRegistry(Secret);
        var nimbus = await NimbusHarness.ConfigureAsync(World, registry.Url, Secret, reservationRequired: true);

        registry.NextReservation = MakeReservation("198.51.100.4");
        ITestPlayer dave = await World.JoinPlayer("dave");
        await World.Until(() => nimbus.GetForwardedPlayer(dave.Player.PlayerUID) != null);

        var consume = registry.Requests.Single(r => r.Path == "/api/reservations/consume-by-uid");
        Assert.Equal("POST", consume.Method);
        Assert.Equal(dave.Player.PlayerUID, consume.Uid);
        Assert.Equal("backend-test", consume.Target);
        Assert.True(consume.HasSignatureHeaders, "all four X-Nimbus-* headers must be present");
        Assert.True(consume.SignatureValid,
            "HMAC signature must verify against an independent reimplementation of the canonical string");
    }

    [AtlasScenario]
    public async Task Join_WhenRegistryIsUnreachable_FailsOpenByDefault()
    {
        // Default trade-off (#14): a registry outage must not lock everyone out, so with
        // ReservationRequired=true and the registry DOWN the player is still admitted.
        var nimbus = await NimbusHarness.ConfigureAsync(
            World, "http://127.0.0.1:9/", Secret, reservationRequired: true); // discard port: refused

        ITestPlayer erin = await World.JoinPlayer("erin");
        string uid = erin.Player.PlayerUID;

        await World.Ticks(60); // plenty for a connection-refused round trip

        Assert.True(erin.IsConnected, "default behavior is fail-open when the registry is unreachable");
        Assert.Null(nimbus.GetForwardedPlayer(uid));
    }

    [AtlasScenario]
    public async Task Join_WhenRegistryIsUnreachable_AndFailClosed_IsKicked()
    {
        // Opt-in strict mode (#14): the operator chose that an unreachable registry cannot
        // be used to bypass the proxy, so the player is kicked instead of admitted.
        await NimbusHarness.ConfigureAsync(
            World, "http://127.0.0.1:9/", Secret, reservationRequired: true,
            failClosedWhenRegistryUnreachable: true);

        bool kicked = false;
        World.Api.Event.PlayerDisconnect += p => { if (p.PlayerName == "frank") kicked = true; };

        try
        {
            await World.JoinPlayer("frank");
        }
        catch (AtlasSetupException)
        {
            return; // kick landed during the join handshake; that is the expected outcome
        }

        await World.Until(() => kicked, timeoutTicks: 300);
    }

    [AtlasScenario]
    public async Task Forwarding_IsCleared_WhenThePlayerDisconnects()
    {
        using var registry = new FakeRegistry(Secret);
        var nimbus = await NimbusHarness.ConfigureAsync(World, registry.Url, Secret, reservationRequired: true);

        registry.NextReservation = MakeReservation("192.0.2.33");
        ITestPlayer peggy = await World.JoinPlayer("peggy");
        string uid = peggy.Player.PlayerUID;
        await World.Until(() => nimbus.GetForwardedPlayer(uid) != null);

        // Scenario bodies run on the game thread, so a direct server-side disconnect is safe.
        peggy.Player.Disconnect("leaving for the forwarding-cleanup scenario");
        await World.Until(() => !peggy.IsConnected);

        Assert.Null(nimbus.GetForwardedPlayer(uid));
    }
}
