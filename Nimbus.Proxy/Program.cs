using Nimbus.Shared;

namespace Nimbus.Proxy;

internal static class Program
{
    private const string ConfigFileName = "nimbus.proxy.toml";

    private static async Task<int> Main(string[] args)
    {
        Log.Info($"Nimbus {NimbusProtocol.NimbusVersion} starting");
        UpdateChecker.StartBackgroundCheck();

        ProxyConfig cfg;
        try { cfg = LoadConfig(); }
        catch (Exception ex) { Log.Error("config load failed: " + ex.Message); return 2; }

        Log.Configure(cfg.Logging.Verbose);
        try
        {
            var validation = ProxyConfigValidator.Validate(cfg);
            foreach (var warning in validation.Warnings)
                Log.Warn("config warning: " + warning);
            if (!validation.IsValid)
            {
                foreach (var error in validation.Errors)
                    Log.Error("config error: " + error);
                return 2;
            }
        }
        catch (Exception ex) { Log.Error("config invalid: " + ex.Message); return 2; }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => e.Cancel = HandleCancelKey(cts);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => RequestShutdown(cts);

        ProxyRegistryHost registryHost;
        try
        {
            registryHost = ProxyRegistryHost.Build(cfg, cts.Token);
        }
        catch (Exception ex) { Log.Error("registry init failed: " + ex.Message); return 2; }

        await using var registryDispose = registryHost;
        using var runtime = new ProxyRuntime(cfg, cts.Token, registryHost.Client, LoadConfig);

        try
        {
            await runtime.RunAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error("fatal: " + ex);
            return 1;
        }
        return 0;
    }

    // Cancels the run on behalf of a handler that outlives the source it cancels.
    //
    // Both handlers above can fire once that source is disposed. ProcessExit runs during teardown,
    // after Main has returned and its `using` has run, which is every exit path there is: a config
    // the validator refused, a registry that could not bind, a clean 0. Under a SIGTERM it is the
    // other way round, raised while Main is still on its way out, so the dispose races a handler
    // that is already running. Cancel() on a disposed source throws, a throw out of an event
    // handler has nowhere to go, and an unhandled exception during teardown is what the process
    // exits on instead of the code Main decided: the refusal #95 was reported against left 134 and
    // a stack trace where the service log wanted a 2.
    //
    // Unregistering on the way out would close the first path and neither race, so the handlers
    // tolerate a disposed source rather than assume they run before it.
    //
    // Returns whether there was still a run to cancel.
    internal static bool RequestShutdown(CancellationTokenSource cts)
    {
        try
        {
            cts.Cancel();
            return true;
        }
        catch (ObjectDisposedException) { return false; }
    }

    // Whether ctrl+c is taken over, which is what the caller assigns to e.Cancel. Taking it over
    // is what turns the keypress into a graceful shutdown instead of an immediate kill, and it is
    // only worth doing while there is something left to shut down. Once the source is gone the run
    // is already over, and swallowing the keypress there would leave an operator holding ctrl+c on
    // a process that is not listening for it any more.
    internal static bool HandleCancelKey(CancellationTokenSource cts)
    {
        if (!RequestShutdown(cts)) return false;
        Log.Info("ctrl+c received, shutting down");
        return true;
    }

    internal static ProxyConfig LoadConfig()
    {
        var path = Path.Combine(AppContext.BaseDirectory, ConfigFileName);
        var jsonSibling = Path.ChangeExtension(path, ".json");
        bool existed = File.Exists(path);

        // Noted before the rename below, which moves the very file a later File.Exists would be
        // asking about. Reading it afterwards would answer "no legacy config" on exactly the runs
        // that had one, and this flag decides whether the run counts as a migration.
        bool hadLegacy = !existed && File.Exists(jsonSibling);

        // The legacy nimbus.proxy.json shape (pre-Velocity layout) doesn't map onto the new
        // schema. Move it aside so LoadOrCreate writes a fresh default TOML rather than
        // picking up incompatible fields.
        bool resetFromLegacy = false;
        if (hadLegacy)
        {
            try
            {
                File.Move(jsonSibling, jsonSibling + ".obsolete", overwrite: true);
                Log.Warn($"renamed legacy {jsonSibling} -> {jsonSibling}.obsolete");
                resetFromLegacy = true;
            }
            catch { /* a read-only install directory keeps the stale file, which is inert:
                       LoadOrCreate only reads the .json sibling when the .toml is missing, and
                       the next line writes one */ }
        }
        bool minted = !existed && !hadLegacy && WriteFirstRunConfig(path);
        var cfg = TomlConfig.LoadOrCreate<ProxyConfig>(path);
        if (!existed)
        {
            Log.Warn($"no config at {path}, wrote defaults");
            if (minted)
                Log.Warn("registry.embedded_shared_secret was generated for this install; copy it into \"SharedSecret\" in each backend's nimbus-server.json");
            // The old settings are gone and the replacements are the shipped ones, which are built
            // for a single machine. That combination starts cleanly, so without this line the only
            // symptom is backends on other hosts quietly failing to reach the registry.
            if (resetFromLegacy)
                Log.Warn($"the legacy config's settings were not carried over; registry.embedded_bind in the new file is loopback and registry.embedded_shared_secret is the placeholder, so review both in {path} before your backends reconnect");
        }
        return cfg;
    }

    // The file an operator has never seen, written before it is read back and validated. Two
    // things separate it from a plain serialization of the defaults:
    //
    //   - the registry shared secret is minted here rather than shipped as a literal, so two
    //     installs never share one and nothing published in a doc opens either (#40),
    //   - the value is useless to the operator without knowing where its twin goes, so the line
    //     above it says so.
    //
    // Skipped when the run started with a legacy nimbus.proxy.json next to it, because that is a
    // migration of an existing network whose backends already have a secret. Minting one there
    // would hand the operator a credential to distribute on a network that never asked for a new
    // one, under a log line written for first-time installs.
    //
    // Returns whether a secret was minted, so the caller only tells the operator to go copy one
    // when there is one to copy.
    internal static bool WriteFirstRunConfig(string path)
    {
        try
        {
            var fresh = new ProxyConfig();
            fresh.Registry.EmbeddedSharedSecret = SecretGenerator.NewSharedSecret();
            TomlConfig.Save(path, fresh);
            AnnotateSharedSecret(path);
            return true;
        }
        catch (Exception ex)
        {
            // LoadOrCreate writes the plain defaults right after this, and a config carrying the
            // known placeholder is one the validator refuses to serve on a public bind. Losing the
            // generated secret is worth a line in the log, not a failed start.
            Log.Warn("could not write a generated registry secret into the new config: " + ex.Message);
            return false;
        }
    }

    // The lines AnnotateSharedSecret puts above the generated secret, held once rather than rebuilt
    // as an array argument on every call.
    private static readonly string[] SharedSecretNote =
    {
        "# Generated for this install. Every backend authenticates to the registry with it:",
        "# copy this exact value into \"SharedSecret\" in each backend's nimbus-server.json.",
        "# Changing it here means changing it on every backend in the same pass.",
    };

    // Tomlyn serializes a POCO, so the only way a comment reaches the file is to put it there
    // afterwards. Best effort by design: a missing key leaves the file exactly as written, since a
    // valid config without a comment beats a mangled one.
    private static void AnnotateSharedSecret(string path)
    {
        const string key = "embedded_shared_secret = ";
        var lines = File.ReadAllLines(path).ToList();
        int at = lines.FindIndex(l => l.StartsWith(key, StringComparison.Ordinal));
        if (at < 0) return;
        lines.InsertRange(at, SharedSecretNote);
        File.WriteAllLines(path, lines, new System.Text.UTF8Encoding(false));
    }
}
