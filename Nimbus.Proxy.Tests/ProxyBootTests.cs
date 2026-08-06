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
