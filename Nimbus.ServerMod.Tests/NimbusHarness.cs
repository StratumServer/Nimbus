using System.Reflection;
using Atlas.Api;
using Vintagestory.API.Common;

namespace Nimbus.ServerMod.Tests;

/// <summary>
/// Drives the loaded NimbusServerModSystem instance by reflection.
///
/// Why reflection instead of a project reference: the game's ModLoader loads a COPY of the
/// staged Nimbus.ServerMod.dll, so its types are never identity-equal to types from a
/// compile-time reference, so a typed cast would always fail. Reflection against the staged
/// assembly is the only shape that works today.
///
/// Why post-boot rewiring at all: the mod reads nimbus-server.json once in StartServerSide
/// and only creates its registry client there; Atlas creates the server's data path
/// internally, so there is no way to plant the config file before boot. Rewiring the private
/// config/registry fields after boot reproduces the configured state. A small testability
/// change in Nimbus (re-wiring the registry client on `/nimbus reload`) would remove the
/// need for most of this class.
/// </summary>
public sealed class NimbusHarness
{
    private readonly ModSystem modSystem;
    private readonly Type modType;

    private NimbusHarness(ModSystem modSystem)
    {
        this.modSystem = modSystem;
        modType = modSystem.GetType();
    }

    public static NimbusHarness Attach(
        IWorldSession world, string registryUrl, string sharedSecret, bool reservationRequired)
    {
        ModSystem ms = world.Api.ModLoader.Systems
            .FirstOrDefault(s => s.GetType().FullName == "Nimbus.ServerMod.NimbusServerModSystem")
            ?? throw new InvalidOperationException(
                "NimbusServerModSystem not loaded: check the AtlasMods staging paths and the server logs.");

        var harness = new NimbusHarness(ms);
        Assembly asm = ms.GetType().Assembly;

        Type cfgType = asm.GetType("Nimbus.ServerMod.NimbusServerConfig")
            ?? throw new InvalidOperationException("NimbusServerConfig type not found in staged assembly.");
        object cfg = Activator.CreateInstance(cfgType)!;
        SetProp(cfg, "Enabled", true);
        SetProp(cfg, "ServerId", "backend-test");
        SetProp(cfg, "DisplayName", "Atlas backend");
        SetProp(cfg, "PublicHost", "127.0.0.1");
        SetProp(cfg, "RegistryUrl", registryUrl);
        SetProp(cfg, "SharedSecret", sharedSecret);
        SetProp(cfg, "ReservationRequired", reservationRequired);

        Type clientType = asm.GetType("Nimbus.ServerMod.NimbusRegistryClient")
            ?? throw new InvalidOperationException("NimbusRegistryClient type not found in staged assembly.");
        object client = Activator.CreateInstance(
            clientType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null, args: new[] { cfg }, culture: null)!;

        harness.DisposeCurrentClient();
        harness.SetField("config", cfg);
        harness.SetField("registry", client);
        return harness;
    }

    /// <summary>Restores the mod to its unwired state (registry = null → gating off).</summary>
    public void Detach()
    {
        DisposeCurrentClient();
        SetField("registry", null);
    }

    /// <summary>Calls the mod's public GetForwardedPlayer(uid); null when not forwarded.</summary>
    public object? GetForwardedPlayer(string playerUid)
        => modType.GetMethod("GetForwardedPlayer")!.Invoke(modSystem, new object[] { playerUid });

    /// <summary>RealRemoteIp recorded on the consumed reservation, or null.</summary>
    public string? ForwardedRealIp(string playerUid)
    {
        object? reservation = GetForwardedPlayer(playerUid);
        return reservation?.GetType().GetProperty("RealRemoteIp")?.GetValue(reservation) as string;
    }

    private void DisposeCurrentClient()
    {
        if (GetField("registry") is IDisposable d) d.Dispose();
    }

    private object? GetField(string name)
        => modType.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(modSystem);

    private void SetField(string name, object? value)
        => modType.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(modSystem, value);

    private static void SetProp(object target, string name, object value)
        => target.GetType().GetProperty(name)!.SetValue(target, value);
}
