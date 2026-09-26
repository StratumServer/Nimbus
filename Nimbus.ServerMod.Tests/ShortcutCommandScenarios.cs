using System.Text.Json;
using Atlas.Api;
using Atlas.XUnit;
using Xunit;

namespace Nimbus.ServerMod.Tests;

/// <summary>
/// Covers the config-driven shortcut commands (/hub, /lobby, ...). Vintage Story registers chat
/// commands during StartServerSide and has no way to unregister one, so the shortcut table has
/// to be on disk before the server boots: [AtlasDataFiles] seeds nimbus-server.json into
/// ModConfig, and the file itself declares the shortcuts these scenarios exercise.
///
/// The seeded config points the registry at a dead port on purpose. Registration and target
/// resolution are what is under test here, and resolution reads the snapshot the heartbeat loop
/// filled, so scenarios that need a live target rewire the mod to a fake registry first.
/// </summary>
[AtlasDataFiles("data/shortcuts/nimbus-server.json", TargetPath = "ModConfig")]
public class ShortcutCommandScenarios : AtlasScenarioBase
{
    private const string Secret = "shortcut-secret";

    private const string ShortcutsJson = """
        [
          { "Name": "hub", "Targets": [ "hub2" ] },
          { "Name": "lobby", "Targets": [ "survival-lobby", "hub2" ], "Description": "Back to your lobby" },
          { "Name": "staff", "Targets": [ "staff" ], "Privilege": "controlserver" },
          { "Name": "home", "Targets": [ "backend-test" ] },
          { "Name": "tp", "Targets": [ "hub2" ] },
          { "Name": "broken", "Targets": [] }
        ]
        """;

    private async Task WaitForSnapshot(string backendId)
    {
        for (int i = 0; i < 100; i++)
        {
            CommandResult servers = await World.ExecuteCommand("/nimbus servers");
            if (servers.Message.Contains(backendId)) return;
            await World.Ticks(10);
        }
        throw new Xunit.Sdk.XunitException($"registry snapshot never listed '{backendId}'");
    }

    // The status text only says a transfer began; the intent the registry received is what moves
    // the player, so check that it was posted, for whom, to where and on whose behalf.
    private async Task AssertIntentPosted(FakeRegistry registry, ITestPlayer player, string target)
    {
        await World.Until(() => registry.Requests.Any(r => r.Path == "/api/transfer-intents"));
        var intent = registry.Requests.Last(r => r.Path == "/api/transfer-intents");
        Assert.True(intent.SignatureValid, "transfer intents must be HMAC-signed");

        using JsonDocument body = JsonDocument.Parse(intent.Body);
        Assert.Equal(player.Player.PlayerUID, body.RootElement.GetProperty("PlayerUid").GetString());
        Assert.Equal(target, body.RootElement.GetProperty("TargetServerId").GetString());
        Assert.Equal("player:" + player.Player.PlayerUID, body.RootElement.GetProperty("RequestedBy").GetString());
    }

    [AtlasScenario]
    public async Task Shortcut_TransfersToItsTarget()
    {
        using var registry = new FakeRegistry(Secret);
        registry.ServersSnapshot = FakeRegistry.Snapshot(FakeRegistry.Backend("hub2"));
        registry.TransferIntentResponse = new { ok = true };
        await NimbusHarness.ConfigureAsync(World, registry.Url, Secret,
            reservationRequired: false, shortcutCommandsJson: ShortcutsJson);

        ITestPlayer alice = await World.JoinPlayer("alice");
        await WaitForSnapshot("hub2");

        CommandResult hub = await alice.ExecuteCommand("/hub");

        Assert.True(hub.Ok, hub.Message);
        Assert.Contains("hub2", hub.Message);
        await AssertIntentPosted(registry, alice, "hub2");
    }

    [AtlasScenario]
    public async Task Shortcut_FallsThroughToTheNextTarget_WhenTheFirstIsMissing()
    {
        using var registry = new FakeRegistry(Secret);
        // survival-lobby is not registered, so /lobby must land on its second choice.
        registry.ServersSnapshot = FakeRegistry.Snapshot(FakeRegistry.Backend("hub2"));
        registry.TransferIntentResponse = new { ok = true };
        await NimbusHarness.ConfigureAsync(World, registry.Url, Secret,
            reservationRequired: false, shortcutCommandsJson: ShortcutsJson);

        ITestPlayer bob = await World.JoinPlayer("bob");
        await WaitForSnapshot("hub2");

        CommandResult lobby = await bob.ExecuteCommand("/lobby");

        Assert.True(lobby.Ok, lobby.Message);
        Assert.Contains("hub2", lobby.Message);
    }

    [AtlasScenario]
    public async Task Shortcut_SkipsATargetInMaintenance()
    {
        using var registry = new FakeRegistry(Secret);
        // First choice exists but is closed; the chain must keep going rather than fail.
        registry.ServersSnapshot = FakeRegistry.Snapshot(
            FakeRegistry.Backend("survival-lobby", maintenance: true),
            FakeRegistry.Backend("hub2"));
        registry.TransferIntentResponse = new { ok = true };
        await NimbusHarness.ConfigureAsync(World, registry.Url, Secret,
            reservationRequired: false, shortcutCommandsJson: ShortcutsJson);

        ITestPlayer carol = await World.JoinPlayer("carol");
        await WaitForSnapshot("hub2");

        CommandResult lobby = await carol.ExecuteCommand("/lobby");

        Assert.True(lobby.Ok, lobby.Message);
        Assert.Contains("hub2", lobby.Message);
    }

    [AtlasScenario]
    public async Task Shortcut_ExplainsItselfWhenNoTargetIsAvailable()
    {
        using var registry = new FakeRegistry(Secret);
        registry.ServersSnapshot = FakeRegistry.Snapshot(FakeRegistry.Backend("hub2"));
        await NimbusHarness.ConfigureAsync(World, registry.Url, Secret,
            reservationRequired: false, shortcutCommandsJson: ShortcutsJson);

        ITestPlayer dave = await World.JoinPlayer("dave");
        await WaitForSnapshot("hub2");

        // 'staff' is never in the snapshot: the player deserves a reason, not silence.
        CommandResult staff = await dave.ExecuteCommand("/staff");

        CommandAssert.RefusedByTheHandler(staff, "No server available");
    }

    [AtlasScenario]
    public async Task Shortcut_PointingAtThisServer_SaysYouAreAlreadyThere()
    {
        using var registry = new FakeRegistry(Secret);
        registry.ServersSnapshot = FakeRegistry.Snapshot(FakeRegistry.Backend("hub2"));
        await NimbusHarness.ConfigureAsync(World, registry.Url, Secret,
            reservationRequired: false, shortcutCommandsJson: ShortcutsJson);

        ITestPlayer erin = await World.JoinPlayer("erin");
        await WaitForSnapshot("hub2");

        CommandResult home = await erin.ExecuteCommand("/home");

        CommandAssert.RefusedByTheHandler(home, "already there");
    }

    [AtlasScenario]
    public async Task Shortcut_DoesNotShadowAVanillaCommand()
    {
        using var registry = new FakeRegistry(Secret);
        registry.ServersSnapshot = FakeRegistry.Snapshot(FakeRegistry.Backend("hub2"));
        await NimbusHarness.ConfigureAsync(World, registry.Url, Secret,
            reservationRequired: false, shortcutCommandsJson: ShortcutsJson);

        // The seeded config asks for a /tp shortcut. /tp is vanilla teleport, so the shortcut has
        // to be refused rather than hijacked, or teleporting to coordinates would break.
        //
        // Asserted on the registration rather than by running /tp: vanilla's handler throws when
        // called without an entity, which is exactly what a console-run /tp does here.
        var tp = World.Api.ChatCommands.Get("tp");

        Assert.NotNull(tp);
        Assert.DoesNotContain("Move yourself to hub2", tp!.Description ?? "");
    }

    [AtlasScenario]
    public async Task Shortcut_RetargetedByReload_UsesTheNewTarget()
    {
        using var registry = new FakeRegistry(Secret);
        registry.ServersSnapshot = FakeRegistry.Snapshot(
            FakeRegistry.Backend("hub2"), FakeRegistry.Backend("creative"));
        registry.TransferIntentResponse = new { ok = true };
        await NimbusHarness.ConfigureAsync(World, registry.Url, Secret,
            reservationRequired: false, shortcutCommandsJson: ShortcutsJson);

        ITestPlayer frank = await World.JoinPlayer("frank");
        await WaitForSnapshot("creative");

        // The command is registered at boot, but its targets are read per call, so a reload can
        // retarget an existing shortcut without a restart.
        await NimbusHarness.ConfigureAsync(World, registry.Url, Secret,
            reservationRequired: false,
            shortcutCommandsJson: """[ { "Name": "hub", "Targets": [ "creative" ] } ]""");

        CommandResult hub = await frank.ExecuteCommand("/hub");

        Assert.True(hub.Ok, hub.Message);
        Assert.Contains("creative", hub.Message);
        await AssertIntentPosted(registry, frank, "creative");
    }

    [AtlasScenario]
    public async Task Shortcut_TightenedByReload_DeniesAPlayerWhoLostAccess()
    {
        using var registry = new FakeRegistry(Secret);
        registry.ServersSnapshot = FakeRegistry.Snapshot(FakeRegistry.Backend("hub2"));
        registry.TransferIntentResponse = new { ok = true };
        await NimbusHarness.ConfigureAsync(World, registry.Url, Secret,
            reservationRequired: false, shortcutCommandsJson: ShortcutsJson);

        ITestPlayer gina = await World.JoinPlayer("gina");
        await WaitForSnapshot("hub2");

        // A joined test player is admin by default (IsSinglePlayerClient). Downgrade gina to an
        // ordinary role up front: "everyone gets through" before the reload, and the refusal
        // after it, both need a real non-admin player to mean anything. Nothing between the
        // downgrade and the last assertion is a command Atlas's ExecuteCommand docs list as
        // putting a test player back on the highest-privilege role (/op, /self role,
        // /group create, /player role or privilege, whitelist, a rejoin, a mod granting or
        // denying a privilege), and the privilege check is repeated before the call that relies
        // on it. The role is put back in a finally anyway, so the next scenario in this world
        // starts from the player it joined as.
        string originalRole = gina.Player.Role.Code;
        gina.Player.SetRole("suplayer");
        try
        {
            Assert.False(gina.Player.HasPrivilege("controlserver"), "test setup: player should not be privileged here");

            // Open to everyone at boot: an ordinary player gets through.
            CommandResult before = await gina.ExecuteCommand("/hub");
            Assert.True(before.Ok, before.Message);

            // The operator locks it down and reloads. The mod does not touch the privilege the engine
            // gate was registered with, so without the handler-side re-check this silently stays open.
            await NimbusHarness.ConfigureAsync(World, registry.Url, Secret,
                reservationRequired: false,
                shortcutCommandsJson: """[ { "Name": "hub", "Targets": [ "hub2" ], "Privilege": "controlserver" } ]""");

            Assert.False(gina.Player.HasPrivilege("controlserver"), "the reload must not have restored the player's role");
            CommandResult after = await gina.ExecuteCommand("/hub");

            // The engine gate still lets "chat" through (an engine refusal would read "noprivilege"),
            // so this refusal is the handler's own re-check. The empty error code pins that mechanism:
            // a reload that re-applied RequiresPrivilege to the command would also refuse, but with
            // "noprivilege", and would need this assertion changed.
            CommandAssert.RefusedByTheHandler(after, "permission");
        }
        finally
        {
            gina.Player.SetRole(originalRole);
        }
    }
}
