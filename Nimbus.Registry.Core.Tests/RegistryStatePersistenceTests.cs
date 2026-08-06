using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nimbus.Registry.Services;
using Nimbus.Shared.Models;
using Xunit;

namespace Nimbus.Registry.Core.Tests;

/// <summary>
/// The ban list and the whitelist across a restart. Until this existed, an operator who banned
/// a griefer yesterday and rebooted the registry today had an unbanned griefer and no signal
/// that anything had been lost (#79), which the tests here are meant to keep from coming back.
///
/// The "restart" in every test is a second store built over the same directory: that is exactly
/// what the next process does, and it is the only way the file is ever read.
/// </summary>
public sealed class RegistryStatePersistenceTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "nimbus-state-tests-" + Guid.NewGuid().ToString("N"));
    private readonly FakeClock clock = new();

    public void Dispose()
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* never created */ }
    }

    /// <summary>Captures what the store said and at which level, so "log loudly" can be
    /// asserted rather than taken on trust.</summary>
    private sealed class RecordingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Lines { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex,
            Func<TState, Exception?, string> formatter)
            => Lines.Add((level, formatter(state, ex)));

        public string ErrorText => string.Join("\n", Lines.Where(l => l.Level >= LogLevel.Error).Select(l => l.Message));
    }

    private BanStore NewBanStore(ILogger? log = null)
        => new(clock, RegistryStateFiles.Bans(dir, log));

    private WhitelistStore NewWhitelistStore(ILogger? log = null)
        => new(clock, RegistryStateFiles.Whitelist(dir, log));

    private string BansPath => Path.Combine(dir, RegistryStateFiles.BansFileName);
    private string WhitelistPath => Path.Combine(dir, RegistryStateFiles.WhitelistFileName);

    // ---- round trip ----

    [Fact]
    public void ABanOutlivesTheProcessThatMadeIt()
    {
        NewBanStore().Add(new NetworkBan
        {
            PlayerUid = "uid-1",
            PlayerName = "griefer",
            ServerId = "creative",
            Reason = "griefing",
            BannedBy = "admin",
            CreatedAtUnix = clock.NowUnix,
        });

        var restarted = NewBanStore();

        var ban = Assert.Single(restarted.Active());
        Assert.Equal("uid-1", ban.PlayerUid);
        Assert.Equal("griefer", ban.PlayerName);
        Assert.Equal("creative", ban.ServerId);
        Assert.Equal("griefing", ban.Reason);
        Assert.Equal("admin", ban.BannedBy);
        Assert.Equal(clock.NowUnix, ban.CreatedAtUnix);
        // The whole point: the gate answers the same after the restart as before it.
        Assert.NotNull(restarted.FindBlocking("uid-1", "creative"));
    }

    [Fact]
    public void AWhitelistEntryOutlivesTheProcessThatMadeIt()
    {
        NewWhitelistStore().Add(new WhitelistEntry
        {
            PlayerUid = "uid-2",
            PlayerName = "builder",
            ServerId = "staff",
            Note = "trusted",
            AddedBy = "admin",
            CreatedAtUnix = clock.NowUnix,
        });

        var restarted = NewWhitelistStore();

        var entry = Assert.Single(restarted.Active());
        Assert.Equal("trusted", entry.Note);
        Assert.Equal("admin", entry.AddedBy);
        Assert.NotNull(restarted.FindCovering("uid-2", "staff"));
        // And an empty whitelist after a restart of a closed network would lock everyone out,
        // so the scoped entry has to come back scoped and not network-wide.
        Assert.Null(restarted.FindCovering("uid-2", ""));
    }

    [Fact]
    public void ATimedBanThatRanOutWhileTheRegistryWasDown_DoesNotComeBack()
    {
        var store = NewBanStore();
        store.Add(new NetworkBan { PlayerUid = "temporary", ExpiresAtUnix = clock.NowUnix + 3600 });
        store.Add(new NetworkBan { PlayerUid = "permanent" });

        clock.Advance(TimeSpan.FromSeconds(3601));
        var restarted = NewBanStore();

        // Serving an hour and then getting the hour back because the registry rebooted is the
        // failure this replaces.
        Assert.Equal("permanent", Assert.Single(restarted.Active()).PlayerUid);
        Assert.Null(restarted.FindBlocking("temporary"));
        // And the expired one is gone from the file too, so it is not re-read forever.
        Assert.DoesNotContain("temporary", File.ReadAllText(BansPath));
    }

    [Fact]
    public void ATimedWhitelistEntryThatRanOutWhileTheRegistryWasDown_DoesNotComeBack()
    {
        var store = NewWhitelistStore();
        store.Add(new WhitelistEntry { PlayerUid = "day-pass", ExpiresAtUnix = clock.NowUnix + 86400 });

        clock.Advance(TimeSpan.FromSeconds(86401));

        Assert.Empty(NewWhitelistStore().Active());
    }

    [Fact]
    public void AnUnchangedListIsNotRewrittenOnEveryBoot()
    {
        NewBanStore().Add(new NetworkBan { PlayerUid = "uid-1" });
        var written = File.GetLastWriteTimeUtc(BansPath);

        NewBanStore();

        // Only a list that disagrees with its file gets written back at load, so a registry
        // that restarts in a loop does not churn the file it is trying to protect.
        Assert.Equal(written, File.GetLastWriteTimeUtc(BansPath));
    }

    // ---- write-through ----

    [Fact]
    public void EveryMutationReachesTheFileImmediately()
    {
        var store = NewBanStore();

        store.Add(new NetworkBan { PlayerUid = "uid-1" });
        Assert.Single(NewBanStore().Active());

        store.Add(new NetworkBan { PlayerUid = "uid-2" });
        Assert.Equal(2, NewBanStore().Active().Count);

        // A lift that only lived in memory would be undone by the next restart, which is the
        // half of write-through that nobody thinks to check.
        Assert.True(store.Lift("uid-1", null));
        Assert.Equal("uid-2", Assert.Single(NewBanStore().Active()).PlayerUid);
    }

    [Fact]
    public void ARemovalFromTheWhitelistReachesTheFileImmediately()
    {
        var store = NewWhitelistStore();
        store.Add(new WhitelistEntry { PlayerUid = "uid-1" });
        store.Add(new WhitelistEntry { PlayerUid = "uid-1", ServerId = "staff" });

        Assert.True(store.Remove("uid-1", "staff"));

        // The network-wide entry is a separate row and stays, which is what lets an operator
        // take somebody off the staff server without ejecting them from the network.
        var restarted = NewWhitelistStore();
        Assert.True(Assert.Single(restarted.Active()).IsNetworkWide);
    }

    [Fact]
    public void ALiftThatMatchedNothingWritesNothing()
    {
        var store = NewBanStore();
        store.Add(new NetworkBan { PlayerUid = "uid-1" });
        var written = File.GetLastWriteTimeUtc(BansPath);

        Assert.False(store.Lift("somebody-else", null));

        Assert.Equal(written, File.GetLastWriteTimeUtc(BansPath));
    }

    [Fact]
    public void TheBackgroundSweepWritesWhatItDropped()
    {
        var store = NewBanStore();
        store.Add(new NetworkBan { PlayerUid = "temporary", ExpiresAtUnix = clock.NowUnix + 60 });
        store.Add(new NetworkBan { PlayerUid = "permanent" });

        clock.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal(1, store.Prune());

        Assert.DoesNotContain("temporary", File.ReadAllText(BansPath));
        Assert.Equal(0, store.Prune());
    }

    [Fact]
    public void TheWriteLeavesNoTemporaryFileBehind()
    {
        NewBanStore().Add(new NetworkBan { PlayerUid = "uid-1" });

        // The temp file plus rename is what makes a write atomic; one left lying around means
        // the rename did not happen and the real file is whatever was there before.
        Assert.False(File.Exists(BansPath + ".tmp"));
        Assert.True(File.Exists(BansPath));
    }

    [Fact]
    public void TheStateDirectoryIsCreatedOnDemand()
    {
        var nested = Path.Combine(dir, "state", "registry");

        new BanStore(clock, RegistryStateFiles.Bans(nested)).Add(new NetworkBan { PlayerUid = "uid-1" });

        Assert.True(File.Exists(Path.Combine(nested, RegistryStateFiles.BansFileName)));
    }

    [Fact]
    public void AStoreWithNoStateFileTouchesTheDiskNotAtAll()
    {
        // The memory-only store is still the shape every unit test uses, and the one a caller
        // gets when it does not want a file.
        new BanStore(clock).Add(new NetworkBan { PlayerUid = "uid-1" });

        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public void AFirstStartWithNoFileIsAnEmptyListAndNotAnError()
    {
        var log = new RecordingLogger();

        Assert.Empty(NewBanStore(log).Active());

        Assert.Empty(log.Lines);
        Assert.False(File.Exists(BansPath + ".bad"));
    }

    // ---- the file itself ----

    [Fact]
    public void TheFileIsReadableJsonAnOperatorCanInspect()
    {
        NewBanStore().Add(new NetworkBan { PlayerUid = "uid-1", Reason = "griefing" });

        using var doc = JsonDocument.Parse(File.ReadAllText(BansPath));

        // The format is part of the contract now: an operator moving a ban list between two
        // registries, or a later token store riding the same layer, reads this shape.
        Assert.True(doc.RootElement.TryGetProperty("updatedAtUnix", out var updated));
        Assert.Equal(clock.NowUnix, updated.GetInt64());
        var entry = Assert.Single(doc.RootElement.GetProperty("entries").EnumerateArray().ToList());
        Assert.Equal("uid-1", entry.GetProperty("playerUid").GetString());
        Assert.Equal("griefing", entry.GetProperty("reason").GetString());
    }

    [Fact]
    public void TheTwoListsGetOneFileEach()
    {
        NewBanStore().Add(new NetworkBan { PlayerUid = "banned" });
        NewWhitelistStore().Add(new WhitelistEntry { PlayerUid = "listed" });

        // Sharing one file would make a corrupt whitelist take the ban list down with it.
        Assert.Contains("banned", File.ReadAllText(BansPath));
        Assert.DoesNotContain("listed", File.ReadAllText(BansPath));
        Assert.Contains("listed", File.ReadAllText(WhitelistPath));
    }

    // ---- corruption ----

    [Fact]
    public void AFileFullOfGarbage_IsRenamedAsideAndSaidOutLoud()
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(BansPath, "this is not json {{{");
        var log = new RecordingLogger();

        var store = NewBanStore(log);

        // Three things at once, and all three matter: the registry still starts, it does not
        // pretend the list was empty, and the file is kept so somebody can look at it.
        Assert.Empty(store.Active());
        Assert.Equal("this is not json {{{", File.ReadAllText(BansPath + ".bad"));
        Assert.False(File.Exists(BansPath));
        Assert.Contains(LogLevel.Error, log.Lines.Select(l => l.Level));
        Assert.Contains(".bad", log.ErrorText);
        Assert.Contains(BansPath, log.ErrorText);
    }

    [Fact]
    public void AFileThatIsValidJsonOfTheWrongShape_IsTreatedAsCorruptToo()
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(WhitelistPath, """{"entries":"not an array"}""");
        var log = new RecordingLogger();

        var store = NewWhitelistStore(log);

        Assert.Empty(store.Active());
        Assert.True(File.Exists(WhitelistPath + ".bad"));
        Assert.Contains(LogLevel.Error, log.Lines.Select(l => l.Level));
    }

    [Fact]
    public void AFileWithNoEntriesArrayAtAll_IsTreatedAsCorrupt()
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(BansPath, """{"updatedAtUnix":123}""");
        var log = new RecordingLogger();

        Assert.Empty(NewBanStore(log).Active());
        Assert.True(File.Exists(BansPath + ".bad"));
        Assert.NotEmpty(log.ErrorText);
    }

    [Fact]
    public void AfterQuarantine_TheNextChangeStartsACleanFile()
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(BansPath, "garbage");
        var store = NewBanStore(new RecordingLogger());

        store.Add(new NetworkBan { PlayerUid = "uid-1" });

        // Starting "from the renamed-aside file's absence" has to mean the registry is usable
        // again straight away, not that it writes nothing until somebody deletes the .bad file.
        Assert.Equal("uid-1", Assert.Single(NewBanStore().Active()).PlayerUid);
        Assert.True(File.Exists(BansPath + ".bad"));
    }

    [Fact]
    public void ASecondCorruptFile_DoesNotFailToRenameOverTheFirstQuarantine()
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(BansPath + ".bad", "an older corruption");
        File.WriteAllText(BansPath, "a newer corruption");

        Assert.Empty(NewBanStore(new RecordingLogger()).Active());

        Assert.Equal("a newer corruption", File.ReadAllText(BansPath + ".bad"));
    }

    [Fact]
    public void WithNoLoggerToHand_TheComplaintStillGetsOut()
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(BansPath, "garbage");
        var captured = new StringWriter();
        var previous = Console.Error;

        try
        {
            Console.SetError(captured);
            // The embedded registry can run with no HTTP listener and therefore no container to
            // build a logger from. Losing the whole ban list quietly because of that would be
            // the same failure this file exists to prevent.
            Assert.Empty(NewBanStore().Active());
        }
        finally
        {
            Console.SetError(previous);
        }

        Assert.Contains(".bad", captured.ToString());
        Assert.Contains("error", captured.ToString());
    }

    [Fact]
    public void AFileThatCannotBeRenamedAside_LeavesTheRegistryRunningAndSaysWhy()
    {
        Directory.CreateDirectory(BansPath + ".bad");
        File.WriteAllText(Path.Combine(BansPath + ".bad", "in-the-way"), "");
        File.WriteAllText(BansPath, "garbage");
        var log = new RecordingLogger();

        Assert.Empty(NewBanStore(log).Active());

        // Refusing to start because the quarantine failed would turn a bad file into an outage.
        Assert.Contains("could not be renamed aside", log.ErrorText);
    }

    [Fact]
    public void AWriteThatFails_LeavesTheBanInForceAndComplains()
    {
        // A directory where the file should be: the rename at the end of the write cannot land.
        Directory.CreateDirectory(BansPath);
        var log = new RecordingLogger();
        var store = NewBanStore(log);

        store.Add(new NetworkBan { PlayerUid = "uid-1" });

        // The ban is enforced either way. What is lost is the next restart, which is exactly
        // what the operator needs told.
        Assert.NotNull(store.FindBlocking("uid-1"));
        Assert.Contains("will not survive a restart", log.ErrorText);
    }

    // ---- wiring ----

    [Fact]
    public void TheHostedRegistryGivesItsStoresTheConfiguredDirectory()
    {
        var cfg = new RegistryConfig { StateDir = dir };

        using (var app = BuildHost(cfg))
        {
            app.Services.GetRequiredService<BanStore>().Add(new NetworkBan { PlayerUid = "uid-1" });
            app.Services.GetRequiredService<WhitelistStore>().Add(new WhitelistEntry { PlayerUid = "uid-2" });
        }

        // A second process reading the same config is the actual restart, and this wiring is
        // what decides whether it sees anything.
        using var restarted = BuildHost(cfg);
        Assert.Equal("uid-1", Assert.Single(restarted.Services.GetRequiredService<BanStore>().Active()).PlayerUid);
        Assert.Equal("uid-2", Assert.Single(restarted.Services.GetRequiredService<WhitelistStore>().Active()).PlayerUid);
    }

    [Fact]
    public void TheHostedRegistryDefaultsToTheWorkingDirectory()
    {
        // Where nimbus.registry.toml is read from. Asserted on the path rather than by writing
        // a file into the test runner's own directory.
        Assert.Equal(Path.Combine(".", RegistryStateFiles.BansFileName),
            RegistryStateFiles.Bans(new RegistryConfig().StateDir).FilePath);
        Assert.Equal(Path.Combine(".", RegistryStateFiles.WhitelistFileName),
            RegistryStateFiles.Whitelist("").FilePath);
    }

    private static WebApplication BuildHost(RegistryConfig cfg)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.AddNimbusRegistry(cfg, withMasterServer: false);
        return builder.Build();
    }
}
