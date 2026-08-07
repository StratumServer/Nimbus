using Atlas.Api;
using Atlas.XUnit;
using Xunit;

namespace Nimbus.ServerMod.Tests;

/// <summary>
/// What the backend does when its SharedSecret is one of the literals Nimbus has published
/// (#40). The mod cannot generate a secret: the proxy or the standalone registry generates one on
/// its first run and this end has to match it, so the only thing left to get right here is
/// refusing to run on a value anyone can read out of the repository, the wiki or a panel's
/// variable screen.
///
/// The placeholders are treated as unset rather than as a secret, which is what the proxy's
/// validator already does for its own copy of the same value.
/// </summary>
public class PlaceholderSecretScenarios : AtlasScenarioBase
{
    private const string Secret = "the-secret-this-network-runs-on";

    /// <summary>Nimbus.Shared's BackendConfig placeholder, spelled out rather than referenced:
    /// this suite loads the mod through the game's ModLoader and holds no assembly reference to
    /// it or to Nimbus.Shared. Writing it out is what an operator's file contains anyway, and a
    /// change to the constant lands here as a failure rather than as a suite agreeing with
    /// itself.</summary>
    private const string WrittenPlaceholder = "PASTE-THE-SECRET-FROM-nimbus.proxy.toml";

    [AtlasScenario]
    public async Task APublishedPlaceholderSecret_StopsTheHeartbeatsInsteadOfSigningWithIt()
    {
        using var registry = new FakeRegistry(Secret);

        // Start from a wired backend, so the negative half below is anchored on a loop that was
        // demonstrably beating once a second rather than on a mod that never started.
        await NimbusHarness.ConfigureAsync(World, registry.Url, Secret);
        await World.Until(() => registry.Requests.Any(r => r.Path == "/api/heartbeat"));

        // The edit this is about: an operator pastes the documented placeholder over a working
        // secret, from the wiki or from a panel variable nobody changed.
        NimbusHarness.WriteConfig(registry.Url, "change-me-and-keep-secret");
        CommandResult reload = await World.ExecuteCommand("/nimbus reload");
        Assert.True(reload.Ok, reload.Message);

        CommandResult status = await World.ExecuteCommand("/nimbus status");
        Assert.Contains("misconfigured", status.Message);

        // Roughly three heartbeat intervals at the test config's one-second cadence. Nothing new
        // may reach the registry: a backend signing with a published value is a backend anyone
        // can impersonate, and the mod stays off the network until the file is fixed.
        int beatsBefore = registry.Requests.Count(r => r.Path == "/api/heartbeat");
        await World.Ticks(90);
        Assert.Equal(beatsBefore, registry.Requests.Count(r => r.Path == "/api/heartbeat"));
    }

    [AtlasScenario]
    public async Task TheValueANewConfigFileIsWrittenWith_IsAlsoRefused()
    {
        // A backend whose nimbus-server.json the mod created for it: the file names the config
        // key to copy the secret out of, and until somebody does, this is not a secret.
        using var registry = new FakeRegistry(Secret);

        NimbusHarness.WriteConfig(registry.Url, WrittenPlaceholder);
        CommandResult reload = await World.ExecuteCommand("/nimbus reload");
        Assert.True(reload.Ok, reload.Message);

        CommandResult status = await World.ExecuteCommand("/nimbus status");
        Assert.Contains("misconfigured", status.Message);

        await World.Ticks(90);
        Assert.DoesNotContain(registry.Requests, r => r.Path == "/api/heartbeat");
    }

    [AtlasScenario]
    public async Task PastingTheRealSecretIn_WiresTheBackendWithoutARestart()
    {
        // The other direction, and the reason the refusal is worth having: the fix is one edit
        // and a reload, on the operator who was just told which key is wrong.
        using var registry = new FakeRegistry(Secret);

        NimbusHarness.WriteConfig(registry.Url, WrittenPlaceholder);
        Assert.True((await World.ExecuteCommand("/nimbus reload")).Ok);

        NimbusHarness.WriteConfig(registry.Url, Secret);
        Assert.True((await World.ExecuteCommand("/nimbus reload")).Ok);

        await World.Until(() => registry.Requests.Any(r => r.Path == "/api/heartbeat"));
        Assert.True(registry.Requests.Last(r => r.Path == "/api/heartbeat").SignatureValid,
            "the heartbeat that finally arrived was not signed with the network's secret");
    }
}
