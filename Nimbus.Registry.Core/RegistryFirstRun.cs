using Nimbus.Shared;

namespace Nimbus.Registry;

// The nimbus.registry.toml a standalone registry writes when it starts in a directory that has
// none. Two things separate it from a plain serialization of the defaults, and they are the same
// two the proxy's first run applies to its embedded registry (#40, #94):
//
//   - shared_secret is generated on the machine that needs it rather than shipped as a literal, so
//     two installs never share one and nothing published in a doc opens either,
//   - the value is useless to the operator without knowing where its twins go, so the lines above
//     it say so.
//
// It lives here rather than in Nimbus.Registry because this is already where the standalone
// registry's config concerns are (RegistryConfig, RegistryConfigWarnings), and because the exe has
// no test project of its own.
public static class RegistryFirstRun
{
    // True when the registry is starting in a directory nobody has configured yet.
    //
    // A legacy nimbus.registry.json sitting next to it does not count: that is an existing network
    // whose backends already hold a secret, TomlConfig.LoadOrCreate migrates the value across, and
    // generating one here would lock out every backend at once under a line written for
    // first-time installs. Asked before anything writes, since both files it looks at are files
    // the loader is about to create or move.
    public static bool IsFirstRun(string configPath)
        => !File.Exists(configPath) && !File.Exists(Path.ChangeExtension(configPath, ".json"));

    // Writes a fresh config at <paramref name="path"/> carrying a generated shared secret.
    // Returns false, with the reason in <paramref name="error"/>, when the file could not be
    // written: the caller then falls through to the plain defaults, which carry the placeholder
    // the warning below already covers. Losing a generated secret is worth a line in the log, not
    // a registry that refuses to start.
    public static bool TryWriteConfig(string path, out string? error)
    {
        try
        {
            TomlConfig.Save(path, new RegistryConfig { SharedSecret = SecretGenerator.NewSharedSecret() });
            AnnotateSharedSecret(path);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    // ConfigAnnotations.InsertAbove does the insertion, shared with the proxy's first run; this
    // only supplies the key it wrote and the note that belongs over a registry's secret, which
    // points at both the backends and the remote proxies.
    //
    // CA1861 is refused here for the reason it is in Nimbus.Proxy/Program.cs: the method runs once
    // per install, so the note stays inline with the key it annotates rather than hoisted to a
    // static field.
#pragma warning disable CA1861
    private static void AnnotateSharedSecret(string path)
        => ConfigAnnotations.InsertAbove(path, "shared_secret = ",
            "# Generated for this install. Everything that talks to this registry authenticates",
            "# with it: copy this exact value into \"SharedSecret\" in each backend's",
            "# nimbus-server.json, and into registry.shared_secret in the nimbus.proxy.toml of",
            "# every proxy running registry.mode = \"remote\" against this registry.",
            "# Changing it here means changing it everywhere else in the same pass.");
#pragma warning restore CA1861
}
