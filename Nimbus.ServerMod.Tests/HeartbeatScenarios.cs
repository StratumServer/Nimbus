using System.Text.Json;
using Atlas.Api;
using Atlas.XUnit;
using Xunit;

namespace Nimbus.ServerMod.Tests;

/// <summary>
/// Covers the heartbeat loop's payload and the /nimbus status and servers commands.
/// The test config sets HeartbeatIntervalSeconds to 1, so beats arrive fast.
/// </summary>
public class HeartbeatScenarios : AtlasScenarioBase
{
    private const string Secret = "heartbeat-secret";

    [AtlasScenario]
    public async Task Heartbeat_CarriesTheServerIdentity_AndIsSigned()
    {
        using var registry = new FakeRegistry(Secret);
        await NimbusHarness.ConfigureAsync(World, registry.Url, Secret, reservationRequired: true);

        await World.Until(() => registry.Requests.Any(r => r.Path == "/api/heartbeat"));
        var beat = registry.Requests.Last(r => r.Path == "/api/heartbeat");

        Assert.True(beat.SignatureValid, "heartbeats must be HMAC-signed");
        using JsonDocument body = JsonDocument.Parse(beat.Body);
        Assert.Equal("backend-test", body.RootElement.GetProperty("ServerId").GetString());
        Assert.Equal("127.0.0.1", body.RootElement.GetProperty("PublicHost").GetString());
        Assert.True(body.RootElement.GetProperty("ReservationRequired").GetBoolean());
        Assert.False(string.IsNullOrEmpty(body.RootElement.GetProperty("GameVersion").GetString()),
            "the heartbeat must report the game version the backend runs");
    }

    [AtlasScenario]
    public async Task StatusCommand_ReportsTheConfiguredIdentity()
    {
        using var registry = new FakeRegistry(Secret);
        await NimbusHarness.ConfigureAsync(World, registry.Url, Secret);

        CommandResult status = await World.ExecuteCommand("/nimbus status");

        Assert.True(status.Ok, status.Message);
        Assert.Contains("backend-test", status.Message);
    }

    [AtlasScenario]
    public async Task ServersCommand_ListsTheSnapshot_WithHealthFlags()
    {
        using var registry = new FakeRegistry(Secret);
        registry.ServersSnapshot = FakeRegistry.Snapshot(
            FakeRegistry.Backend("hub-a"),
            FakeRegistry.Backend("hub-b", maintenance: true));
        await NimbusHarness.ConfigureAsync(World, registry.Url, Secret);

        for (int i = 0; i < 100; i++)
        {
            CommandResult servers = await World.ExecuteCommand("/nimbus servers");
            if (servers.Message.Contains("hub-a"))
            {
                Assert.Contains("hub-b", servers.Message);
                Assert.Contains("maintenance", servers.Message);
                return;
            }
            await World.Ticks(10);
        }
        throw new Xunit.Sdk.XunitException("registry snapshot never reached /nimbus servers");
    }
}
