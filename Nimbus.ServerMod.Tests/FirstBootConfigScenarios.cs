using Atlas.Api;
using Atlas.XUnit;
using Vintagestory.API.Config;
using Xunit;

namespace Nimbus.ServerMod.Tests;

/// <summary>
/// The nimbus-server.json the mod writes for itself when it boots on a server that has none, and
/// which is the only thing a new operator has to go on.
///
/// One scenario, and its own class, because it asserts on the state of the data path before
/// anything in the suite has written a config into it: scenarios share a world with the rest of
/// their class.
/// </summary>
public class FirstBootConfigScenarios : AtlasScenarioBase
{
    [AtlasScenario]
    public async Task ABackendWithNoConfig_IsWrittenOneThatSaysWhereTheSecretComesFrom()
    {
        string path = Path.Combine(GamePaths.DataPath, "ModConfig", "nimbus-server.json");

        Assert.True(File.Exists(path), "the mod left no config file for the operator to edit");
        string written = File.ReadAllText(path);

        // The backend consumes the secret the proxy or the registry generated and cannot generate
        // its own: two independently generated values never match. So the one useful thing this
        // file can do about SharedSecret is name the file to copy it out of (#40).
        Assert.Contains("nimbus.proxy.toml", written, StringComparison.Ordinal);
        Assert.DoesNotContain("change-me-and-keep-secret", written, StringComparison.Ordinal);

        // And until somebody does copy one in, the placeholder is not treated as a secret.
        CommandResult status = await World.ExecuteCommand("/nimbus status");
        Assert.True(status.Ok, status.Message);
        Assert.Contains("misconfigured", status.Message);
    }
}
