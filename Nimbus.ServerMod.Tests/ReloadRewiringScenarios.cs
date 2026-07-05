using Atlas.Api;
using Atlas.XUnit;
using Vintagestory.API.Config;
using Xunit;

namespace Nimbus.ServerMod.Tests;

/// <summary>
/// Exercises the config-file + "/nimbus reload" path end to end: the mod boots
/// unconfigured (no config file exists at server start), the scenario writes
/// nimbus-server.json into the live data path and reloads. No reflection wiring at all;
/// this is exactly what a server operator does.
/// </summary>
public class ReloadRewiringScenarios : AtlasScenarioBase
{
    private const string Secret = "reload-secret";

    private static string ConfigPath
        => Path.Combine(GamePaths.DataPath, "ModConfig", "nimbus-server.json");

    private static void WriteConfig(string registryUrl)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        File.WriteAllText(ConfigPath, $$"""
            {
              "Enabled": true,
              "ServerId": "backend-test",
              "DisplayName": "Atlas backend",
              "PublicHost": "127.0.0.1",
              "RegistryUrl": "{{registryUrl}}",
              "SharedSecret": "{{Secret}}",
              "ReservationRequired": true
            }
            """);
    }

    private static object MakeReservation(string playerName) => new
    {
        id = "res-reload-" + playerName,
        playerUid = "",
        playerName,
        sourceServerId = "hub",
        targetServerId = "backend-test",
        expiresAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 60,
        reason = "reload scenario",
        realRemoteIp = "192.0.2.10",
        realRemotePort = 1,
    };

    [AtlasScenario]
    public async Task Reload_WiresRegistryFromUnconfiguredBoot_AndFollowsUrlChanges()
    {
        using var registryA = new FakeRegistry(Secret);
        using var registryB = new FakeRegistry(Secret);

        // Boots with no config file -> "misconfigured", no registry client. The reload
        // alone must bring the mod to a fully wired state.
        WriteConfig(registryA.Url);
        CommandResult reload = await World.ExecuteCommand("/nimbus reload");
        Assert.True(reload.Ok, reload.Message);

        registryA.NextReservation = MakeReservation("grace");
        ITestPlayer grace = await World.JoinPlayer("grace");
        await World.Until(() => registryA.Requests.Any(r => r.Path.Contains("consume-by-uid")));

        // Point the config at a different registry and reload again: the next consume
        // must land on B, which proves the HTTP client was actually recreated (before
        // the fix, a URL change silently kept talking to A until restart).
        WriteConfig(registryB.Url);
        reload = await World.ExecuteCommand("/nimbus reload");
        Assert.True(reload.Ok, reload.Message);

        registryB.NextReservation = MakeReservation("heidi");
        ITestPlayer heidi = await World.JoinPlayer("heidi");
        string heidiUid = heidi.Player.PlayerUID;
        await World.Until(() => registryB.Requests.Any(
            r => r.Path.Contains("consume-by-uid") && r.Uid == heidiUid));

        Assert.DoesNotContain(registryA.Requests,
            r => r.Path.Contains("consume-by-uid") && r.Uid == heidiUid);
    }
}
