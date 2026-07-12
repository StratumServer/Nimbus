using Atlas.Api;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace Nimbus.ServerMod.Tests;

/// <summary>
/// Configures the mod the way an operator does: write nimbus-server.json into the live
/// data path, run "/nimbus reload". No private state is touched on the way in; since
/// /nimbus reload recreates the registry client (#4), the file + reload pair brings the
/// mod from any state to a fully wired one.
///
/// Reads still go through reflection: the game's ModLoader loads a COPY of the staged
/// Nimbus.ServerMod.dll, so its types are never identity-equal to compile-time
/// references and a typed cast cannot work.
/// </summary>
public sealed class NimbusHarness
{
    private readonly ModSystem modSystem;

    private NimbusHarness(ModSystem modSystem) => this.modSystem = modSystem;

    public static async Task<NimbusHarness> ConfigureAsync(
        IWorldSession world,
        string registryUrl,
        string sharedSecret,
        bool reservationRequired = true,
        string transferMode = "redirect",
        bool allowPlayerServerCommand = true,
        int seamlessPrepareAckTimeoutSeconds = 1)
    {
        WriteConfig(registryUrl, sharedSecret, reservationRequired, transferMode,
            allowPlayerServerCommand, seamlessPrepareAckTimeoutSeconds);

        CommandResult reload = await world.ExecuteCommand("/nimbus reload");
        if (!reload.Ok)
            throw new InvalidOperationException($"/nimbus reload failed: {reload.Message}");

        ModSystem ms = world.Api.ModLoader.Systems
            .FirstOrDefault(s => s.GetType().FullName == "Nimbus.ServerMod.NimbusServerModSystem")
            ?? throw new InvalidOperationException(
                "NimbusServerModSystem not loaded; check the AtlasMods staging paths and the server logs.");
        return new NimbusHarness(ms);
    }

    /// <summary>Writes nimbus-server.json into the embedded server's ModConfig folder.</summary>
    public static void WriteConfig(
        string registryUrl,
        string sharedSecret,
        bool reservationRequired = true,
        string transferMode = "redirect",
        bool allowPlayerServerCommand = true,
        int seamlessPrepareAckTimeoutSeconds = 1)
    {
        string path = Path.Combine(GamePaths.DataPath, "ModConfig", "nimbus-server.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $$"""
            {
              "Enabled": true,
              "ServerId": "backend-test",
              "DisplayName": "Atlas backend",
              "PublicHost": "127.0.0.1",
              "RegistryUrl": "{{registryUrl}}",
              "SharedSecret": "{{sharedSecret}}",
              "ReservationRequired": {{(reservationRequired ? "true" : "false")}},
              "TransferMode": "{{transferMode}}",
              "AllowPlayerServerCommand": {{(allowPlayerServerCommand ? "true" : "false")}},
              "HeartbeatIntervalSeconds": 1,
              "SeamlessPrepareAckTimeoutSeconds": {{seamlessPrepareAckTimeoutSeconds}}
            }
            """);
    }

    /// <summary>Calls the mod's public GetForwardedPlayer(uid); null when not forwarded.</summary>
    public object? GetForwardedPlayer(string playerUid)
        => modSystem.GetType().GetMethod("GetForwardedPlayer")!
            .Invoke(modSystem, new object[] { playerUid });

    /// <summary>RealRemoteIp recorded on the consumed reservation, or null.</summary>
    public string? ForwardedRealIp(string playerUid)
    {
        object? reservation = GetForwardedPlayer(playerUid);
        return reservation?.GetType().GetProperty("RealRemoteIp")?.GetValue(reservation) as string;
    }
}
