using Nimbus.Registry.Services;
using Nimbus.Shared.Models;
using Xunit;

namespace Nimbus.Proxy.Tests;

/// <summary>
/// Embedded mode is the default in nimbus.proxy.toml, and the variant with no HTTP listener
/// builds its stores by hand instead of getting them from a container. That is the path most
/// deployments run, so it is the one where a ban list that dies with the process (#79) would
/// hurt most, and the one where forgetting to wire the state file would go unnoticed.
/// </summary>
public sealed class EmbeddedRegistryStateTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "nimbus-embedded-state-" + Guid.NewGuid().ToString("N"));
    private readonly string relativeDir = "nimbus-embedded-state-" + Guid.NewGuid().ToString("N");

    public void Dispose()
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* never created */ }
        try { Directory.Delete(Path.Combine(AppContext.BaseDirectory, relativeDir), recursive: true); } catch { /* never created */ }
    }

    // No listener: the proxy still keeps its in-process registry, and the config that reaches
    // ProxyRegistryHost is the one an operator wrote.
    private ProxyConfig Config() => new()
    {
        Registry = new RegistryConfig
        {
            Mode = "embedded",
            EmbeddedBind = "",
            ProxyId = "embedded-proxy",
            EmbeddedStateDir = dir,
        },
    };

    [Fact]
    public async Task ABanSurvivesTheProxyItWasAddedOn()
    {
        await using (var host = ProxyRegistryHost.Build(Config(), CancellationToken.None))
        {
            await host.Client!.AddBanAsync(new BanRequest
            {
                PlayerUid = "uid-1",
                PlayerName = "griefer",
                Reason = "griefing",
                BannedBy = "admin",
            }, CancellationToken.None);
        }

        await using var restarted = ProxyRegistryHost.Build(Config(), CancellationToken.None);

        var ban = Assert.Single((await restarted.Client!.GetBansAsync(CancellationToken.None))!);
        Assert.Equal("uid-1", ban.PlayerUid);
        Assert.Equal("griefing", ban.Reason);
    }

    [Fact]
    public async Task AWhitelistEntrySurvivesTheProxyItWasAddedOn()
    {
        await using (var host = ProxyRegistryHost.Build(Config(), CancellationToken.None))
        {
            await host.Client!.AddWhitelistAsync(
                new WhitelistRequest { PlayerUid = "uid-2", ServerId = "staff" }, CancellationToken.None);
        }

        await using var restarted = ProxyRegistryHost.Build(Config(), CancellationToken.None);

        // A closed network that forgets its whitelist on restart locks out everyone rather than
        // letting one person in, so this is the more expensive of the two failures.
        var entry = Assert.Single((await restarted.Client!.GetWhitelistAsync(CancellationToken.None))!);
        Assert.Equal("staff", entry.ServerId);
    }

    [Fact]
    public async Task AnUnbanSurvivesTooRatherThanComingBackOnTheNextBoot()
    {
        await using (var host = ProxyRegistryHost.Build(Config(), CancellationToken.None))
        {
            await host.Client!.AddBanAsync(new BanRequest { PlayerUid = "uid-1" }, CancellationToken.None);
            Assert.True(await host.Client.LiftBanAsync("uid-1", null, CancellationToken.None));
        }

        await using var restarted = ProxyRegistryHost.Build(Config(), CancellationToken.None);

        Assert.Empty((await restarted.Client!.GetBansAsync(CancellationToken.None))!);
    }

    [Fact]
    public async Task TheStateFilesLandWhereTheConfigSaid()
    {
        // Nothing is written at boot: the files appear on the first change.
        await using (ProxyRegistryHost.Build(Config(), CancellationToken.None))
            Assert.False(File.Exists(Path.Combine(dir, RegistryStateFiles.BansFileName)));

        await using var host = ProxyRegistryHost.Build(Config(), CancellationToken.None);
        await host.Client!.AddBanAsync(new BanRequest { PlayerUid = "uid-1" }, CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(dir, RegistryStateFiles.BansFileName)));
    }

    [Fact]
    public async Task ARelativeStateDirectoryHangsOffTheExecutable()
    {
        var cfg = Config();
        cfg.Registry.EmbeddedStateDir = relativeDir;

        await using var host = ProxyRegistryHost.Build(cfg, CancellationToken.None);
        await host.Client!.AddBanAsync(new BanRequest { PlayerUid = "uid-1" }, CancellationToken.None);

        // Same rule as the drain flags: a proxy started by a service manager from some other
        // working directory still finds yesterday's bans.
        Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, relativeDir, RegistryStateFiles.BansFileName)));
    }
}
