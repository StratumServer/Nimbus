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

        CommandResult hub = await NimbusHarness.ExecuteAs(World, alice, "/hub");

        Assert.True(hub.Ok, hub.Message);
        Assert.Contains("hub2", hub.Message);
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

        CommandResult lobby = await NimbusHarness.ExecuteAs(World, bob, "/lobby");

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

        CommandResult lobby = await NimbusHarness.ExecuteAs(World, carol, "/lobby");

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
        CommandResult staff = await NimbusHarness.ExecuteAs(World, dave, "/staff");

        Assert.False(staff.Ok);
        Assert.Contains("No server available", staff.Message);
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

        CommandResult home = await NimbusHarness.ExecuteAs(World, erin, "/home");

        Assert.False(home.Ok);
        Assert.Contains("already there", home.Message);
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

        CommandResult hub = await NimbusHarness.ExecuteAs(World, frank, "/hub");

        Assert.True(hub.Ok, hub.Message);
        Assert.Contains("creative", hub.Message);
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

        // Open to everyone at boot: an ordinary player gets through.
        CommandResult before = await NimbusHarness.ExecuteAs(World, gina, "/hub");
        Assert.True(before.Ok, before.Message);

        // The operator locks it down and reloads. The engine gate keeps the privilege it was
        // registered with, so without the handler-side re-check this silently stays open.
        await NimbusHarness.ConfigureAsync(World, registry.Url, Secret,
            reservationRequired: false,
            shortcutCommandsJson: """[ { "Name": "hub", "Targets": [ "hub2" ], "Privilege": "controlserver" } ]""");

        // Atlas test players join privileged, so take the privilege away explicitly: this is
        // about whether the handler honours the CURRENT config value, not about VS's roles.
        World.Api.Permissions.DenyPrivilege(gina.Player.PlayerUID, "controlserver");
        Assert.False(gina.Player.HasPrivilege("controlserver"), "test setup: player should not be privileged here");

        CommandResult after = await NimbusHarness.ExecuteAs(World, gina, "/hub");

        Assert.False(after.Ok, "tightening a shortcut's privilege must take effect on reload");
        Assert.Contains("permission", after.Message);
    }
}
