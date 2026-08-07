using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Xunit;

namespace Nimbus.Proxy.Tests;

/// <summary>
/// The three pieces between a config file and a proxy that is serving players: the registry the
/// mode setting decides on, the listener that accepts and tracks sessions, and the runtime that
/// owns both plus the plugins and answers `nimctl reload`.
///
/// Real ports throughout. The listener is the only thing that says whether a config actually
/// results in something listening, so faking the socket would test nothing worth knowing.
/// </summary>
public class ProxyBootTests
{
    /// <summary>An ephemeral port the OS has just handed back. These components bind the address
    /// out of config themselves, so they cannot be asked for one after the fact.</summary>
    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static ProxyConfig Config(int bindPort, params RecordingBackend[] backends)
    {
        var cfg = new ProxyConfig
        {
            Bind = $"127.0.0.1:{bindPort}",
            Servers = backends.Select((b, i) => (Id: i == 0 ? "hub" : $"backend{i}", b))
                .ToDictionary(x => x.Id, x => $"127.0.0.1:{x.b.Port}"),
            Try = new List<string> { "hub" },
        };
        cfg.Registry.Mode = "disabled";
        cfg.Admin.Enabled = false;
        cfg.Metrics.Enabled = false;
        cfg.Persistence.PersistDrainFlags = false;
        cfg.Plugins.Enabled = false;
        return cfg;
    }

    // ---- the listener ----

    [Fact]
    public async Task TheListener_AcceptsPlayersAndPutsThemInTheSessionTable()
    {
        using var backend = new RecordingBackend();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        int port = FreePort();
        var listener = new ProxyListener(Config(port, backend), cts.Token);
        var running = Task.Run(listener.RunAsync);

        using var player = new TcpClient();
        await ConnectWhenReadyAsync(player, port, cts.Token);
        await player.GetStream().WriteAsync(ClientFrames.Identification("uid-1", "alice"), cts.Token);

        await SessionHarness.WaitForAsync(() => listener.Sessions.Count == 1, "the accepted player never appeared in the session table");
        var session = listener.Sessions.Values.Single();
        await SessionHarness.WaitForAsync(() => session.PlayerUid == "uid-1", "the session never captured the uid");

        // Which is what the admin commands walk to find who to kick, and what /status counts.
        Assert.Equal("alice", session.PlayerName);
        await SessionHarness.WaitForAsync(() => backend.Connections == 1, "the session never reached the backend");

        cts.Cancel();
        await running;
    }

    [Fact]
    public async Task TheListener_TakesASessionBackOutOfTheTableWhenItEnds()
    {
        using var backend = new RecordingBackend();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        int port = FreePort();
        var listener = new ProxyListener(Config(port, backend), cts.Token);
        var running = Task.Run(listener.RunAsync);

        using var player = new TcpClient();
        await ConnectWhenReadyAsync(player, port, cts.Token);
        await player.GetStream().WriteAsync(ClientFrames.Identification("uid-1", "alice"), cts.Token);
        await SessionHarness.WaitForAsync(() => listener.Sessions.Count == 1, "the player never appeared");

        // What `nimctl kick` does to the session it found in this table.
        listener.Sessions.Values.Single().Close();

        // A table that only grows is a memory leak and a `list` that lies to the operator about
        // who is still on the network.
        await SessionHarness.WaitForAsync(() => listener.Sessions.IsEmpty, "the closed session stayed in the table");

        cts.Cancel();
        await running;
    }

    [Fact]
    public async Task TheListener_GivesEverySessionItsOwnId()
    {
        using var backend = new RecordingBackend();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        int port = FreePort();
        var listener = new ProxyListener(Config(port, backend), cts.Token);
        var running = Task.Run(listener.RunAsync);

        var players = new List<TcpClient>();
        for (int i = 0; i < 3; i++)
        {
            var player = new TcpClient();
            await ConnectWhenReadyAsync(player, port, cts.Token);
            await player.GetStream().WriteAsync(ClientFrames.Identification($"uid-{i}", $"player{i}"), cts.Token);
            players.Add(player);
        }

        await SessionHarness.WaitForAsync(() => listener.Sessions.Count == 3, "not every player was accepted");
        // The id is what an operator types into `kick`, so two sessions sharing one would kick
        // the wrong player.
        Assert.Equal(3, listener.Sessions.Keys.Distinct().Count());

        foreach (var p in players) p.Close();
        cts.Cancel();
        await running;
    }

    [Fact]
    public async Task TheListener_StopsListeningWhenTheProxyIsShutDown()
    {
        using var backend = new RecordingBackend();
        var cts = new CancellationTokenSource();
        int port = FreePort();
        var listener = new ProxyListener(Config(port, backend), cts.Token);
        var running = Task.Run(listener.RunAsync);
        using (var probe = new TcpClient()) await ConnectWhenReadyAsync(probe, port, CancellationToken.None);

        cts.Cancel();
        await running;

        // The port has to come back, or a restart after ctrl+c fails to bind.
        using var after = new TcpClient();
        await Assert.ThrowsAsync<SocketException>(async () => await after.ConnectAsync(IPAddress.Loopback, port));
        cts.Dispose();
    }

    [Fact]
    public void AListenerWithoutARegistry_StillHasTheGatesTheSessionsConsult()
    {
        using var backend = new RecordingBackend();
        using var cts = new CancellationTokenSource();

        var listener = new ProxyListener(Config(FreePort(), backend), cts.Token);

        // Sessions read these unconditionally. Null caches on a registry-less proxy would be a
        // NullReferenceException on the first join instead of an empty ban list.
        Assert.NotNull(listener.Bans);
        Assert.NotNull(listener.Whitelist);
        Assert.NotNull(listener.Router);
        Assert.NotNull(listener.Stickies);
        Assert.NotNull(listener.UdpOverrides);
        Assert.Null(listener.Registry);
    }

    // ---- what a first run does ----

    /// <summary>A directory with no nimbus.proxy.toml in it, which is what an operator unzips.
    /// The caller gets the path the config will land at.</summary>
    private static string FreshInstallDir(out string configPath)
    {
        string dir = Path.Combine(Path.GetTempPath(), "nimbus-firstrun-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        configPath = Path.Combine(dir, "nimbus.proxy.toml");
        return dir;
    }

    /// <summary>Program.LoadConfig's write half against a chosen directory: mint the config, then
    /// read it back with the same loader Main uses.</summary>
    private static ProxyConfig FirstRun(string configPath)
    {
        Assert.True(Program.WriteFirstRunConfig(configPath), "the first run reported no generated secret");
        return Nimbus.Shared.TomlConfig.LoadOrCreate<ProxyConfig>(configPath);
    }

    [Fact]
    public async Task AFreshInstall_WritesAConfigThatBootsWithoutBeingEditedFirst()
    {
        // Program.Main's opening sequence on an empty directory: no nimbus.proxy.toml, so one is
        // written, read back off disk and validated before anything binds. Until #87 that ended at
        // the validator with exit 2. Going through the file rather than through `new ProxyConfig()`
        // is the point: it is the written bytes an operator ends up running.
        string dir = FreshInstallDir(out string path);
        try
        {
            var cfg = FirstRun(path);
            Assert.True(File.Exists(path), "first run left no config file for the operator to edit");

            var validation = ProxyConfigValidator.Validate(cfg);
            Assert.True(validation.IsValid, string.Join("; ", validation.Errors));

            // Past the validator, boot continues into the embedded registry. The written bind is
            // loopback:8765, which a build agent may already have something on, so this asks for
            // the same loopback host on a port the OS just handed back.
            Assert.StartsWith("http://127.0.0.1:", cfg.Registry.EmbeddedBind);
            cfg.Registry.EmbeddedBind = $"http://127.0.0.1:{FreePort()}";

            await using var host = ProxyRegistryHost.Build(cfg, CancellationToken.None);

            Assert.IsType<InProcRegistryClient>(host.Client);
            using var probe = new TcpClient();
            await probe.ConnectAsync(IPAddress.Loopback, new Uri(cfg.Registry.EmbeddedBind).Port);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* a leftover temp dir is harmless */ }
        }
    }

    [Fact]
    public void AFreshInstall_MintsItsOwnRegistrySecretInsteadOfShippingOne()
    {
        string dir = FreshInstallDir(out string path);
        try
        {
            var cfg = FirstRun(path);

            // A literal in the source is a literal in every install that never edited it, and the
            // value is in a public repository (#40).
            Assert.NotEqual("change-me-and-keep-secret", cfg.Registry.EmbeddedSharedSecret);
            Assert.NotEqual("REPLACE_ME_WITH_A_LONG_RANDOM_STRING", cfg.Registry.EmbeddedSharedSecret);
            Assert.Equal(Nimbus.Shared.SecretGenerator.Length, cfg.Registry.EmbeddedSharedSecret.Length);
            Assert.All(cfg.Registry.EmbeddedSharedSecret, c => Assert.True(char.IsAsciiLetterOrDigit(c),
                $"'{c}' would need quoting or escaping somewhere on its way to a backend"));
            // The operator retypes this into every backend, sometimes off a panel screen, so a
            // character that is only distinguishable in some fonts costs them an HMAC failure.
            Assert.All(cfg.Registry.EmbeddedSharedSecret, c => Assert.DoesNotContain(c, "0Oo1lI"));

            // Generated or not, the file still has to pass the check that reads it back.
            var validation = ProxyConfigValidator.Validate(cfg);
            Assert.True(validation.IsValid, string.Join("; ", validation.Errors));

            // A secret is no use to the operator without the other end of it, and the file is the
            // only place they are looking.
            Assert.Contains("nimbus-server.json", File.ReadAllText(path), StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* a leftover temp dir is harmless */ }
        }
    }

    [Fact]
    public void TwoFreshInstalls_DoNotEndUpSharingASecret()
    {
        string first = FreshInstallDir(out string firstPath);
        string second = FreshInstallDir(out string secondPath);
        try
        {
            // Two installs that share a secret are one install as far as the registry is
            // concerned: either network's backends can heartbeat into the other.
            Assert.NotEqual(FirstRun(firstPath).Registry.EmbeddedSharedSecret,
                FirstRun(secondPath).Registry.EmbeddedSharedSecret);
        }
        finally
        {
            try { Directory.Delete(first, recursive: true); } catch { /* harmless */ }
            try { Directory.Delete(second, recursive: true); } catch { /* harmless */ }
        }
    }

    [Fact]
    public void AnExistingConfigOnASecondRun_IsLeftAloneRatherThanReminted()
    {
        string dir = FreshInstallDir(out string path);
        try
        {
            var written = FirstRun(path);

            // Program.LoadConfig only writes when the file is missing. Regenerating on every boot
            // would silently cut every backend off from the registry after a restart.
            var reread = Nimbus.Shared.TomlConfig.LoadOrCreate<ProxyConfig>(path);

            Assert.Equal(written.Registry.EmbeddedSharedSecret, reread.Registry.EmbeddedSharedSecret);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* harmless */ }
        }
    }

    [Fact]
    public void AFirstRunThatCannotWriteItsConfig_GivesUpOnTheSecretRatherThanTheBoot()
    {
        // A read-only install directory, or one the service account cannot reach. The loader falls
        // through to the plain defaults, which still start on loopback; the placeholder secret they
        // carry is the one the validator refuses on any wider bind, so nothing is quietly exposed.
        string unreachable = Path.Combine(
            Path.GetTempPath(), "nimbus-no-such-dir-" + Guid.NewGuid().ToString("N"), "nimbus.proxy.toml");

        Assert.False(Program.WriteFirstRunConfig(unreachable));
        Assert.False(File.Exists(unreachable));
    }

    /// <summary>Program.LoadConfig resolves its own paths from AppContext.BaseDirectory, which
    /// under the test runner is this assembly's output folder, so driving the real loader means
    /// working in it. This clears the three files it touches and puts them back afterwards.
    /// Nothing else in the suite reads them.</summary>
    private sealed class BaseDirectoryInstall : IDisposable
    {
        public string Toml { get; } = Path.Combine(AppContext.BaseDirectory, "nimbus.proxy.toml");
        public string LegacyJson { get; } = Path.Combine(AppContext.BaseDirectory, "nimbus.proxy.json");
        public string Obsolete => LegacyJson + ".obsolete";
        public string Migrated => LegacyJson + ".migrated";

        private readonly Dictionary<string, string> saved = new();

        private string[] All => new[] { Toml, LegacyJson, Obsolete, Migrated };

        public static BaseDirectoryInstall Clean()
        {
            var install = new BaseDirectoryInstall();
            foreach (var p in install.All)
            {
                if (!File.Exists(p)) continue;
                install.saved[p] = File.ReadAllText(p);
                File.Delete(p);
            }
            return install;
        }

        public void Dispose()
        {
            foreach (var p in All)
            {
                try
                {
                    if (saved.TryGetValue(p, out var text)) File.WriteAllText(p, text);
                    else File.Delete(p);
                }
                catch { /* the runner's output folder is rebuilt anyway */ }
            }
        }
    }

    [Fact]
    public void TheLoaderTheProxyActuallyBootsWith_WritesOnceNextToTheBinaryAndRereadsIt()
    {
        // The one place the whole first-run sequence runs as Main runs it.
        using var install = BaseDirectoryInstall.Clean();

        var first = Program.LoadConfig();

        Assert.True(File.Exists(install.Toml), "the boot loader left no config file behind");
        Assert.NotEqual("change-me-and-keep-secret", first.Registry.EmbeddedSharedSecret);
        var validation = ProxyConfigValidator.Validate(first);
        Assert.True(validation.IsValid, string.Join("; ", validation.Errors));

        // The second boot, and every `nimctl reload`, reads what the first one wrote.
        Assert.Equal(first.Registry.EmbeddedSharedSecret,
            Program.LoadConfig().Registry.EmbeddedSharedSecret);
    }

    [Fact]
    public void ALegacyJsonNextToTheBinary_IsAMigrationAndMintsNoSecret()
    {
        using var install = BaseDirectoryInstall.Clean();

        // The pre-Velocity config an upgrade finds. Its shape does not map onto the current schema,
        // so LoadConfig moves it aside and the defaults are written instead. That reset predates
        // this PR and is not what this test is about.
        File.WriteAllText(install.LegacyJson, "{\"Bind\":\"0.0.0.0:42420\"}");

        var cfg = Program.LoadConfig();

        Assert.True(File.Exists(install.Obsolete), "the legacy config was not moved aside");
        Assert.False(File.Exists(install.LegacyJson));

        // The point. This is an existing network: its backends already hold a shared secret, and
        // minting one here would hand the operator a fresh credential to go distribute, under a log
        // line written for first-time installs. The skip used to be read from the .json path after
        // the rename above had already deleted it, so it only ever fired when the rename threw.
        Assert.Equal("change-me-and-keep-secret", cfg.Registry.EmbeddedSharedSecret);
    }

    [Fact]
    public void ALegacyJsonThatCannotBeMovedAside_IsStillAMigrationAndMintsNoSecret()
    {
        using var install = BaseDirectoryInstall.Clean();

        // The rename fails when the install directory is read-only. A directory sitting where the
        // .obsolete file wants to go makes File.Move throw without needing the whole folder
        // read-only. LoadOrCreate then finds the .json still there and takes its own migration
        // branch, which carries the legacy values into the new TOML and leaves the original as
        // .json.migrated. So this is the path where the network's real secret does survive, and
        // minting over it would lock out every backend at once.
        File.WriteAllText(install.LegacyJson,
            "{\"Bind\":\"0.0.0.0:42420\",\"Registry\":{\"EmbeddedSharedSecret\":\"the-secret-this-network-runs-on\"}}");
        Directory.CreateDirectory(install.Obsolete);
        try
        {
            var cfg = Program.LoadConfig();

            Assert.True(File.Exists(install.Migrated), "the legacy config was not migrated");
            Assert.Equal("the-secret-this-network-runs-on", cfg.Registry.EmbeddedSharedSecret);
            Assert.Contains("the-secret-this-network-runs-on", File.ReadAllText(install.Toml), StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(install.Obsolete, recursive: true); } catch { /* harmless */ }
        }
    }

    [Fact]
    public void AFreshInstallWidenedToOffBoxBackendsWithoutASecret_IsStillRefused()
    {
        // The one edit an off-box deployment makes, made carelessly, by an operator who pasted the
        // placeholder out of a doc rather than keeping what the first run generated. The loopback
        // default is safe because this stays an error: it is the whole reason moving the default is
        // not a weakening.
        var cfg = new ProxyConfig();
        cfg.Registry.EmbeddedBind = "http://0.0.0.0:8765";

        var result = ProxyConfigValidator.Validate(cfg);

        Assert.False(result.IsValid);
        Assert.Equal("registry.embedded_bind is not loopback, so registry.embedded_shared_secret must be changed from the default",
            Assert.Single(result.Errors));
    }

    // ---- the registry the mode setting picks ----

    [Theory]
    [InlineData("disabled")]
    [InlineData("")]
    [InlineData("  DISABLED  ")]
    public async Task RegistryModeDisabled_LeavesTheProxyWithNoRegistryClient(string mode)
    {
        var cfg = Config(FreePort());
        cfg.Registry.Mode = mode;

        await using var host = ProxyRegistryHost.Build(cfg, CancellationToken.None);

        Assert.Null(host.Client);
    }

    [Fact]
    public async Task AnUnrecognisedRegistryMode_IsTreatedAsDisabledRatherThanCrashingTheBoot()
    {
        var cfg = Config(FreePort());
        cfg.Registry.Mode = "cloud";

        // The validator refuses this before boot, so reaching here means something bypassed it.
        // Standing the registry down beats taking the proxy with it.
        await using var host = ProxyRegistryHost.Build(cfg, CancellationToken.None);

        Assert.Null(host.Client);
    }

    [Fact]
    public async Task RegistryModeRemote_BuildsAnHttpClientPointedAtTheConfiguredRegistry()
    {
        var cfg = Config(FreePort());
        cfg.Registry.Mode = "remote";
        cfg.Registry.Url = "https://registry.example";
        cfg.Registry.SharedSecret = "shared";

        await using var host = ProxyRegistryHost.Build(cfg, CancellationToken.None);

        Assert.IsType<HttpRegistryClient>(host.Client);
    }

    [Theory]
    [InlineData("", "shared")]
    [InlineData("https://registry.example", "")]
    public async Task RegistryModeRemoteWithHalfItsSettings_StandsDownRatherThanFailingEveryCall(
        string url, string secret)
    {
        var cfg = Config(FreePort());
        cfg.Registry.Mode = "remote";
        cfg.Registry.Url = url;
        cfg.Registry.SharedSecret = secret;

        await using var host = ProxyRegistryHost.Build(cfg, CancellationToken.None);

        // A client with no url or no secret would refuse every reservation, which reads as
        // transfers being broken rather than as a registry that was never configured.
        Assert.Null(host.Client);
    }

    [Fact]
    public async Task RegistryModeEmbeddedWithNoHttpBind_StillGivesTheProxyAnInProcessRegistry()
    {
        var cfg = Config(FreePort());
        cfg.Registry.Mode = "embedded";
        cfg.Registry.EmbeddedBind = "";

        await using var host = ProxyRegistryHost.Build(cfg, CancellationToken.None);

        // Single-process deployments where every backend is local: no HTTP listener, but
        // reservations and bans still work.
        Assert.IsType<InProcRegistryClient>(host.Client);
    }

    [Fact]
    public async Task RegistryModeEmbedded_ServesBackendsOverHttpAndAnswersInProcess()
    {
        var cfg = Config(FreePort());
        cfg.Registry.Mode = "embedded";
        cfg.Registry.EmbeddedBind = $"http://127.0.0.1:{FreePort()}";
        cfg.Registry.EmbeddedSharedSecret = "a-long-random-string";

        await using var host = ProxyRegistryHost.Build(cfg, CancellationToken.None);

        Assert.IsType<InProcRegistryClient>(host.Client);
        // The listener is what off-box backends heartbeat against, so it has to actually be up.
        using var probe = new TcpClient();
        await probe.ConnectAsync(IPAddress.Loopback, new Uri(cfg.Registry.EmbeddedBind).Port);
    }

    [Fact]
    public void AnEmbeddedRegistryThatCannotBind_SaysWhichAddressRatherThanThrowingRaw()
    {
        var taken = new TcpListener(IPAddress.Loopback, 0);
        taken.Start();
        int port = ((IPEndPoint)taken.LocalEndpoint).Port;

        var cfg = Config(FreePort());
        cfg.Registry.Mode = "embedded";
        cfg.Registry.EmbeddedBind = $"http://127.0.0.1:{port}";
        cfg.Registry.EmbeddedSharedSecret = "a-long-random-string";

        try
        {
            var ex = Assert.Throws<InvalidOperationException>(
                () => ProxyRegistryHost.Build(cfg, CancellationToken.None));
            // Program.Main turns this into "registry init failed: ...", so the address the
            // operator has to free needs to be in the message.
            Assert.Contains(cfg.Registry.EmbeddedBind, ex.Message);
        }
        finally { taken.Stop(); }
    }

    [Fact]
    public async Task DisposingTheRegistryHost_TakesTheEmbeddedListenerDown()
    {
        var cfg = Config(FreePort());
        cfg.Registry.Mode = "embedded";
        int registryPort = FreePort();
        cfg.Registry.EmbeddedBind = $"http://127.0.0.1:{registryPort}";
        cfg.Registry.EmbeddedSharedSecret = "a-long-random-string";

        var host = ProxyRegistryHost.Build(cfg, CancellationToken.None);
        await host.DisposeAsync();

        using var after = new TcpClient();
        await Assert.ThrowsAsync<SocketException>(async () => await after.ConnectAsync(IPAddress.Loopback, registryPort));
    }

    // ---- the runtime and what `nimctl reload` does ----

    [Fact]
    public void TheRuntime_LoadsThePluginsDirectoryAtStartup()
    {
        using var plugins = PluginDir.With(PluginDir.Sample);
        using var backend = new RecordingBackend();
        using var cts = new CancellationTokenSource();
        var cfg = Config(FreePort(), backend);
        cfg.Plugins.Enabled = true;
        cfg.Plugins.Directory = plugins.Path;

        using var runtime = new ProxyRuntime(cfg, cts.Token, registry: null, () => cfg);

        // Nothing here asserts on the loader itself; what matters is that the runtime points it
        // at the configured directory rather than at wherever the binary happens to live.
        Assert.Contains("1 plugin(s)", runtime.Reload());
    }

    [Fact]
    public void Reload_ReportsWhatTheProxyIsRunningAfterwards()
    {
        using var backend = new RecordingBackend();
        using var second = new RecordingBackend();
        using var cts = new CancellationTokenSource();
        var cfg = Config(FreePort(), backend);
        var fresh = Config(FreePort(), backend, second);
        using var runtime = new ProxyRuntime(cfg, cts.Token, registry: null, () => fresh);

        string result = runtime.Reload();

        Assert.Equal("2 server(s), 0 plugin(s)", result);
        // The live config object is updated in place, so the router and the sessions already
        // holding a reference to it see the new pool without a restart.
        Assert.Equal(2, cfg.Servers.Count);
        Assert.True(cfg.Servers.ContainsKey("backend1"));
    }

    [Fact]
    public void ReloadingAConfigThatWouldNotBoot_ChangesNothingAndSaysWhy()
    {
        using var backend = new RecordingBackend();
        using var cts = new CancellationTokenSource();
        var cfg = Config(FreePort(), backend);
        var broken = Config(FreePort(), backend);
        broken.Advanced.BufferSize = 16;
        broken.Status.Name = "";
        using var runtime = new ProxyRuntime(cfg, cts.Token, registry: null, () => broken);

        string result = runtime.Reload();

        // A reload that half-applied a bad config would take a working proxy down between two
        // player joins, so it is refused whole and the running one is untouched.
        Assert.StartsWith("reload failed: ", result);
        Assert.Contains("advanced.buffer_size must be at least 1024", result);
        Assert.Contains("status.name must be set", result);
        Assert.Equal(16 * 1024, cfg.Advanced.BufferSize);
    }

    [Fact]
    public void ReloadingWhenTheConfigFileCannotBeRead_SaysSoRatherThanThrowing()
    {
        using var backend = new RecordingBackend();
        using var cts = new CancellationTokenSource();
        var cfg = Config(FreePort(), backend);
        using var runtime = new ProxyRuntime(cfg, cts.Token, registry: null,
            () => throw new InvalidDataException("bind: must be 'host:port', got 'nonsense'"));

        string result = runtime.Reload();

        // The operator typed `nimctl reload` and gets an answer on the socket. A throw here comes
        // out of the admin command handler instead.
        Assert.StartsWith("reload failed: config error: ", result);
        Assert.Contains("must be 'host:port'", result);
    }

    [Fact]
    public void Reload_AppliesAChangedPluginSetWithoutARestart()
    {
        using var plugins = PluginDir.With(PluginDir.Sample);
        using var backend = new RecordingBackend();
        using var cts = new CancellationTokenSource();
        var cfg = Config(FreePort(), backend);
        cfg.Plugins.Enabled = true;
        cfg.Plugins.Directory = plugins.Path;

        var fresh = Config(FreePort(), backend);
        fresh.Plugins.Enabled = true;
        fresh.Plugins.Directory = plugins.Path;
        fresh.Plugins.Disabled = new List<string> { "hub-fallback" };

        using var runtime = new ProxyRuntime(cfg, cts.Token, registry: null, () => fresh);

        Assert.Equal("1 server(s), 0 plugin(s)", runtime.Reload());
    }

    [Fact]
    public void Reload_TurnsTheNewVerbosityOn()
    {
        using var backend = new RecordingBackend();
        using var cts = new CancellationTokenSource();
        var cfg = Config(FreePort(), backend);
        var fresh = Config(FreePort(), backend);
        fresh.Logging.Verbose = true;
        using var runtime = new ProxyRuntime(cfg, cts.Token, registry: null, () => fresh);

        runtime.Reload();

        // Turning trace logging on without dropping every player is most of the point of reload.
        Assert.True(cfg.Logging.Verbose);
        Log.Configure(verbose: false);
    }

    // ---- how a run ends ----

    /// <summary>
    /// A copy of the built proxy in a directory of its own, with the config the test wants it to
    /// boot on next to it. Program resolves nimbus.proxy.toml from AppContext.BaseDirectory, so
    /// that directory is the only handle a test has on which config the real binary reads, and
    /// running the binary is the only way to see what it exits with. The copy is staged next to
    /// the test binaries by the StageProxyApp target in the csproj.
    /// </summary>
    private sealed class ProxyInstall : IDisposable
    {
        private static readonly string Staged = Path.Combine(AppContext.BaseDirectory, "proxy-app");

        private readonly string dir;

        public ProxyInstall()
        {
            dir = Path.Combine(Path.GetTempPath(), "nimbus-exit-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            Assert.True(File.Exists(Path.Combine(Staged, "Nimbus.Proxy.dll")),
                $"no staged proxy build in {Staged}, so there is nothing to boot");
            foreach (var file in Directory.GetFiles(Staged))
                File.Copy(file, Path.Combine(dir, Path.GetFileName(file)));
        }

        public void Write(ProxyConfig cfg) =>
            Nimbus.Shared.TomlConfig.Save(Path.Combine(dir, "nimbus.proxy.toml"), cfg);

        /// <summary>Boots it and waits for it to be over. Everything the operator would have in
        /// front of them comes back together, since which stream a crash lands on is part of what
        /// is being asserted.</summary>
        public async Task<(int ExitCode, string Output)> RunToCompletionAsync()
        {
            // The muxer belonging to the runtime this test host is already on, located from where
            // that runtime was loaded from: shared/Microsoft.NETCore.App/<version>, three levels
            // under the install root. Asking PATH for `dotnet` instead would pick whichever install
            // happens to be first on a machine that has several, which need not be the one the
            // proxy was built against, and the apphost next to the dll resolves its runtime the
            // same unreliable way.
            string runtime = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
            string muxer = Path.Combine(
                Path.GetFullPath(Path.Combine(runtime, "..", "..", "..")),
                OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
            Assert.True(File.Exists(muxer), $"no dotnet host at {muxer} to boot the proxy with");

            var start = new ProcessStartInfo(muxer)
            {
                WorkingDirectory = dir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            start.ArgumentList.Add("exec");
            start.ArgumentList.Add(Path.Combine(dir, "Nimbus.Proxy.dll"));

            using var proxy = Process.Start(start)!;
            var stdout = proxy.StandardOutput.ReadToEndAsync();
            var stderr = proxy.StandardError.ReadToEndAsync();
            using var giveUp = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            try
            {
                await proxy.WaitForExitAsync(giveUp.Token);
            }
            catch (OperationCanceledException)
            {
                proxy.Kill(entireProcessTree: true);
                Assert.Fail("the proxy never exited");
            }
            return (proxy.ExitCode, await stdout + await stderr);
        }

        public void Dispose()
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* a leftover temp dir is harmless */ }
        }
    }

    [Fact]
    public async Task ARegistryThatCannotBind_ExitsTwoRatherThanCrashingOnTheWayOut()
    {
        // The refusal #94 was tested against: the registry port is already taken, Main says so and
        // returns 2. What came after the return was the bug. The ProcessExit handler fired during
        // teardown and cancelled the source Main's `using` had just disposed, which throws, and a
        // throw out of an event handler has nowhere to go, so the orderly refusal reached the
        // service log as a stack trace and a crash code instead of the line naming the address to
        // free (#95).
        var taken = new TcpListener(IPAddress.Loopback, 0);
        taken.Start();
        try
        {
            using var install = new ProxyInstall();
            var cfg = new ProxyConfig();
            cfg.Registry.Mode = "embedded";
            cfg.Registry.EmbeddedBind = $"http://127.0.0.1:{((IPEndPoint)taken.LocalEndpoint).Port}";
            cfg.Registry.EmbeddedSharedSecret = "a-long-random-string";
            install.Write(cfg);

            var (exitCode, output) = await install.RunToCompletionAsync();

            // 2 is what a supervisor reads as "this one is not coming back until someone edits
            // something", as against a crash it should restart into.
            Assert.Equal(2, exitCode);
            Assert.Contains("registry init failed", output, StringComparison.Ordinal);
            Assert.Contains(cfg.Registry.EmbeddedBind, output, StringComparison.Ordinal);
            Assert.DoesNotContain("Unhandled exception", output, StringComparison.Ordinal);
            Assert.DoesNotContain(nameof(ObjectDisposedException), output, StringComparison.Ordinal);
        }
        finally { taken.Stop(); }
    }

    [Fact]
    public void ShutdownRequestedWhileTheProxyIsRunning_CancelsTheRun()
    {
        using var cts = new CancellationTokenSource();

        Assert.True(Program.RequestShutdown(cts));
        Assert.True(cts.IsCancellationRequested);
    }

    [Fact]
    public void ShutdownRequestedOnceTheRunIsOver_IsAcceptedQuietly()
    {
        // ProcessExit fires after Main has returned, so the source it was given is always already
        // disposed by then. Every exit path goes through here, including the successful one.
        var disposed = new CancellationTokenSource();
        disposed.Dispose();

        Assert.False(Program.RequestShutdown(disposed));
    }

    [Fact]
    public void CtrlCWhileTheProxyIsRunning_IsTakenOverAndShutsItDown()
    {
        using var cts = new CancellationTokenSource();

        // The true is what the handler assigns to e.Cancel, which is what stops the runtime from
        // being killed outright and lets the sessions be closed properly.
        Assert.True(Program.HandleCancelKey(cts));
        Assert.True(cts.IsCancellationRequested);
    }

    [Fact]
    public void CtrlCOnceTheRunIsOver_IsLeftToWhoeverHandlesItNext()
    {
        // Ctrl+c can land in the same window ProcessExit runs in, and would have thrown out of the
        // handler the same way. There is no run left to cancel there, so the keypress is not taken
        // over either: swallowing it would leave an operator pressing ctrl+c at a process that has
        // stopped listening for it.
        //
        // Pinned here rather than against the running binary on purpose. ConsoleCancelEventArgs has
        // no public constructor, so the registered handler cannot be invoked from outside the
        // runtime, and provoking the real thing means landing a SIGINT inside the gap between
        // Main's return and the process going away, which is a race a test would lose most runs.
        var disposed = new CancellationTokenSource();
        disposed.Dispose();

        Assert.False(Program.HandleCancelKey(disposed));
    }

    private static async Task ConnectWhenReadyAsync(TcpClient client, int port, CancellationToken ct)
    {
        for (int attempt = 0; attempt < 100; attempt++)
        {
            try
            {
                await client.ConnectAsync(IPAddress.Loopback, port, ct);
                return;
            }
            catch (SocketException) { await Task.Delay(20, ct); }
        }
        Assert.Fail($"the proxy never came up on 127.0.0.1:{port}");
    }
}
