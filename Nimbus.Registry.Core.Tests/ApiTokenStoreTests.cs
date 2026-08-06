using System.Text.Json;
using Nimbus.Registry.Services;
using Nimbus.Shared.Models;
using Xunit;

namespace Nimbus.Registry.Core.Tests;

/// <summary>
/// The token lifecycle, and the one property everything else rests on: the secret exists in the
/// create response and nowhere else, on disk least of all. A store that kept the plaintext would
/// turn a leaked state file into a full compromise of every credential ever issued, which is the
/// failure the hash exists to prevent, so it is asserted against the actual bytes of the actual
/// file rather than against the model.
///
/// The "restart" in every persistence test here is a second store built over the same directory,
/// because that is exactly what the next process does.
/// </summary>
public sealed class ApiTokenStoreTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "nimbus-token-tests-" + Guid.NewGuid().ToString("N"));
    private readonly FakeClock clock = new();

    public void Dispose()
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* never created */ }
    }

    private ApiTokenStore NewStore() => new(clock, RegistryStateFiles.Tokens(dir));

    private ApiTokenService NewService() => new(NewStore(), clock);

    private string TokensPath => Path.Combine(dir, RegistryStateFiles.TokensFileName);

    private static ApiTokenCreateRequest Request(string name = "discord-bot", params string[] scopes)
        => new()
        {
            Name = name,
            Scopes = (scopes.Length == 0 ? new[] { ApiTokenScopes.WhitelistWrite } : scopes).ToList(),
            CreatedBy = "admin",
        };

    // ---- minting ----

    [Fact]
    public void ANewTokenCarriesThePrefixAndTheFullDraw()
    {
        var result = NewService().Create(Request());

        Assert.Equal(ApiTokenCreateStatus.Ok, result.Status);
        Assert.StartsWith(ApiTokenSecret.Prefix, result.Plaintext);
        Assert.Equal(ApiTokenSecret.Prefix.Length + ApiTokenSecret.SecretChars, result.Plaintext.Length);
        // Base62 and nothing else: the string ends up in shell commands and TOML files, where
        // base64's +, / and = are the characters that get mangled.
        Assert.All(result.Plaintext[ApiTokenSecret.Prefix.Length..], c => Assert.True(char.IsAsciiLetterOrDigit(c)));
    }

    [Fact]
    public void TwoTokensNeverCollide()
    {
        var service = NewService();
        var secrets = new HashSet<string>(StringComparer.Ordinal);
        var ids = new HashSet<string>(StringComparer.Ordinal);

        for (int i = 0; i < 200; i++)
        {
            var result = service.Create(Request($"bot-{i}"));
            Assert.True(secrets.Add(result.Plaintext), "the generator repeated a secret");
            Assert.True(ids.Add(result.Token!.Id), "the generator repeated an id");
        }
    }

    [Fact]
    public void TheStoreKeepsTheHashAndNotTheSecret()
    {
        var store = NewStore();
        var result = new ApiTokenService(store, clock).Create(Request());

        var held = store.FindByHash(ApiTokenSecret.Hash(result.Plaintext));
        Assert.NotNull(held);
        Assert.Equal(result.Token!.Id, held!.Id);
        Assert.Equal(ApiTokenSecret.Hash(result.Plaintext), held.Hash);
        Assert.DoesNotContain(result.Plaintext, held.Hash, StringComparison.Ordinal);
    }

    [Fact]
    public void TheFileOnDiskNeverHoldsThePlaintext()
    {
        var result = NewService().Create(Request());

        string raw = File.ReadAllText(TokensPath);
        Assert.DoesNotContain(result.Plaintext, raw, StringComparison.Ordinal);
        // Not even the random part without the prefix, which is the form a careless serializer
        // would leave behind.
        Assert.DoesNotContain(result.Plaintext[ApiTokenSecret.Prefix.Length..], raw, StringComparison.Ordinal);
        Assert.Contains(result.Token!.Hash, raw, StringComparison.Ordinal);
    }

    [Fact]
    public void AListingCarriesNeitherTheSecretNorTheHash()
    {
        var service = NewService();
        var result = service.Create(Request());

        var listed = Assert.Single(service.List());
        Assert.Equal(result.Token!.Id, listed.Id);
        Assert.Equal("", listed.Hash);
        Assert.Equal(ApiTokenScopes.WhitelistWrite, Assert.Single(listed.Scopes));
    }

    [Fact]
    public void RedactingLeavesTheStoredRecordAlone()
    {
        var store = NewStore();
        var result = new ApiTokenService(store, clock).Create(Request());

        _ = result.Token!.Redacted();

        Assert.NotEqual("", store.FindById(result.Token.Id)!.Hash);
    }

    // ---- expiry ----

    [Fact]
    public void ATokenExpiresInNinetyDaysUnlessAskedOtherwise()
    {
        var result = NewService().Create(Request());

        Assert.Equal(clock.NowUnix + ApiTokenService.DefaultDurationSeconds, result.Token!.ExpiresAtUnix);
        Assert.True(result.Token.IsUsableAt(clock.NowUnix));
        Assert.False(result.Token.IsUsableAt(result.Token.ExpiresAtUnix));
    }

    [Fact]
    public void PermanenceHasToBeAskedForByName()
    {
        var request = Request();
        request.Permanent = true;

        var result = NewService().Create(request);

        Assert.Equal(0, result.Token!.ExpiresAtUnix);
        Assert.True(result.Token.IsUsableAt(clock.NowUnix + ApiTokenService.DefaultDurationSeconds * 10L));
    }

    [Fact]
    public void ANonsenseDurationFallsBackToTheDefaultRatherThanToPermanence()
    {
        var request = Request();
        request.DurationSeconds = -1;

        var result = NewService().Create(request);

        Assert.Equal(clock.NowUnix + ApiTokenService.DefaultDurationSeconds, result.Token!.ExpiresAtUnix);
    }

    [Fact]
    public void AnExplicitDurationIsTakenAsGiven()
    {
        var request = Request();
        request.DurationSeconds = 3600;

        var result = NewService().Create(request);

        Assert.Equal(clock.NowUnix + 3600, result.Token!.ExpiresAtUnix);
    }

    // ---- refusals ----

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ATokenWithoutANameIsRefused(string name)
    {
        var result = NewService().Create(Request(name));

        Assert.Equal(ApiTokenCreateStatus.MissingName, result.Status);
        Assert.Equal("", result.Plaintext);
        Assert.Null(result.Token);
    }

    [Fact]
    public void ANullRequestIsRefusedRatherThanThrown()
        => Assert.Equal(ApiTokenCreateStatus.MissingName, NewService().Create(null).Status);

    [Theory]
    [InlineData("bot\nBANNED uid-2 by console")]
    [InlineData("bot\rdiscord")]
    [InlineData("bot\tdiscord")]
    public void ANameThatCouldForgeALogLineIsRefused(string name)
    {
        // The name is stamped into BannedBy and written into every log line about the token, so a
        // newline in it is a second log line somebody else wrote.
        var result = NewService().Create(Request(name));

        Assert.Equal(ApiTokenCreateStatus.InvalidName, result.Status);
        Assert.Null(result.Token);
    }

    [Fact]
    public void ANameLongerThanTheLimitIsRefused()
    {
        Assert.Equal(ApiTokenCreateStatus.InvalidName,
            NewService().Create(Request(new string('a', ApiTokenService.MaxNameLength + 1))).Status);
        Assert.Equal(ApiTokenCreateStatus.Ok,
            NewService().Create(Request(new string('a', ApiTokenService.MaxNameLength))).Status);
    }

    [Fact]
    public void ANameIsTrimmedBeforeItIsMeasured()
    {
        var result = NewService().Create(Request("  discord-bot  "));

        Assert.Equal("discord-bot", result.Token!.Name);
    }

    [Fact]
    public void ATokenWithNoScopesIsRefused()
    {
        var request = Request();
        request.Scopes = new List<string> { "", "  " };

        var result = NewService().Create(request);

        Assert.Equal(ApiTokenCreateStatus.NoScopes, result.Status);
    }

    [Fact]
    public void AnUnknownScopeIsRefusedAndNamed()
    {
        var result = NewService().Create(Request("bot", "whitelist:write", "bans:destroy"));

        Assert.Equal(ApiTokenCreateStatus.UnknownScope, result.Status);
        Assert.Equal("bans:destroy", result.UnknownScope);
        // Nothing was minted: a typo that silently dropped the scope would leave the operator
        // holding a token they believe can do something it cannot.
        Assert.Null(result.Token);
    }

    [Fact]
    public void ScopesAreNormalisedAndDeduplicated()
    {
        var result = NewService().Create(Request("bot", " Bans:Write ", "bans:write", "servers:read"));

        Assert.Equal(new[] { ApiTokenScopes.BansWrite, ApiTokenScopes.ServersRead }, result.Token!.Scopes);
    }

    [Fact]
    public void ScopeMatchingIgnoresCase()
    {
        var token = NewService().Create(Request("bot", "bans:write")).Token!;

        Assert.True(token.HasScope("BANS:WRITE"));
        Assert.False(token.HasScope(ApiTokenScopes.BansRead));
    }

    // ---- revocation ----

    [Fact]
    public void RevokingStopsTheTokenWithoutRemovingTheRecord()
    {
        var store = NewStore();
        var service = new ApiTokenService(store, clock);
        var result = service.Create(Request());

        Assert.True(service.Revoke(result.Token!.Id));

        var held = store.FindByHash(result.Token.Hash);
        Assert.NotNull(held);
        Assert.True(held!.Revoked);
        Assert.False(held.IsUsableAt(clock.NowUnix));
        // Still listed: the record is the audit trail, and an absence answers none of the
        // questions asked after a leak.
        Assert.Single(service.List());
    }

    [Fact]
    public void RevokingTwiceIsNotAChange()
    {
        var service = NewService();
        var result = service.Create(Request());

        Assert.True(service.Revoke(result.Token!.Id));
        Assert.False(service.Revoke(result.Token.Id));
    }

    [Theory]
    [InlineData("")]
    [InlineData("no-such-id")]
    public void RevokingSomethingThatDoesNotExistSaysSo(string id)
        => Assert.False(NewService().Revoke(id));

    [Fact]
    public void RevocationSurvivesARestart()
    {
        var first = NewStore();
        var result = new ApiTokenService(first, clock).Create(Request());
        Assert.True(first.Revoke(result.Token!.Id));

        var second = NewStore();

        var held = second.FindByHash(result.Token.Hash);
        Assert.NotNull(held);
        Assert.True(held!.Revoked);
    }

    // ---- persistence ----

    [Fact]
    public void ATokenOutlivesTheProcessThatMintedIt()
    {
        var result = NewService().Create(Request("discord-bot", "whitelist:write", "whitelist:read"));

        var reloaded = NewStore().FindByHash(ApiTokenSecret.Hash(result.Plaintext));

        Assert.NotNull(reloaded);
        Assert.Equal("discord-bot", reloaded!.Name);
        Assert.Equal(result.Token!.Id, reloaded.Id);
        Assert.Equal(result.Token.ExpiresAtUnix, reloaded.ExpiresAtUnix);
        Assert.Equal(new[] { ApiTokenScopes.WhitelistWrite, ApiTokenScopes.WhitelistRead }, reloaded.Scopes);
        Assert.Equal("admin", reloaded.CreatedBy);
    }

    [Fact]
    public void AnExpiredTokenIsKeptAcrossARestartRatherThanDropped()
    {
        var request = Request();
        request.DurationSeconds = 60;
        var result = NewService().Create(request);
        clock.Advance(TimeSpan.FromMinutes(5));

        var reloaded = NewStore();

        // Unlike a ban, which is dropped when it runs out because keeping it would punish
        // somebody who served it, an expired token is kept: it authenticates nobody, and it is
        // the answer to "why did the bot stop working".
        var held = reloaded.FindByHash(result.Token!.Hash);
        Assert.NotNull(held);
        Assert.False(held!.IsUsableAt(clock.NowUnix));
    }

    [Fact]
    public void ARecordWithNoHashIsDroppedAndTheFileRewritten()
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(TokensPath, """
        {
          "entries": [
            { "id": "aaaa", "name": "ghost", "hash": "", "scopes": ["bans:read"] },
            { "id": "bbbb", "name": "real", "hash": "deadbeef", "scopes": ["bans:read"] }
          ],
          "updatedAtUnix": 1
        }
        """);

        var store = NewStore();

        Assert.Equal(1, store.Count);
        Assert.Null(store.FindById("aaaa"));
        Assert.NotNull(store.FindById("bbbb"));
        Assert.DoesNotContain("ghost", File.ReadAllText(TokensPath), StringComparison.Ordinal);
    }

    [Fact]
    public void AnUntouchedStoreDoesNotRewriteItsFileAtBoot()
    {
        NewService().Create(Request());
        string before = File.ReadAllText(TokensPath);
        var stamp = File.GetLastWriteTimeUtc(TokensPath);

        _ = NewStore();

        Assert.Equal(before, File.ReadAllText(TokensPath));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(TokensPath));
    }

    [Fact]
    public void TheTokensLiveInTheirOwnFileNextToTheOtherTwo()
    {
        NewService().Create(Request());

        Assert.True(File.Exists(TokensPath));
        Assert.Equal("nimbus.tokens.json", RegistryStateFiles.TokensFileName);
        // Its own file so a corrupt token list cannot take the ban list down with it.
        Assert.False(File.Exists(Path.Combine(dir, RegistryStateFiles.BansFileName)));
    }

    [Fact]
    public void AStoreWithNoFileKeepsEverythingInMemory()
    {
        var store = new ApiTokenStore(clock);
        var result = new ApiTokenService(store, clock).Create(Request());

        Assert.NotNull(store.FindByHash(result.Token!.Hash));
        Assert.False(Directory.Exists(dir));
    }

    // ---- last use ----

    [Fact]
    public void TheFirstUseIsWrittenThrough()
    {
        var store = NewStore();
        var token = new ApiTokenService(store, clock).Create(Request()).Token!;

        store.RecordUse(token, clock.NowUnix);

        Assert.Equal(clock.NowUnix, Reload(store, token).LastUsedAtUnix);
    }

    [Fact]
    public void ABusyTokenIsNotWrittenToDiskOnEveryCall()
    {
        var store = NewStore();
        var token = new ApiTokenService(store, clock).Create(Request()).Token!;
        store.RecordUse(token, clock.NowUnix);
        long persisted = clock.NowUnix;

        // A bot polling every second would otherwise rewrite the whole file every second to
        // record something nobody reads at that resolution.
        for (int i = 0; i < 30; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            store.RecordUse(token, clock.NowUnix);
        }

        // In memory it is current, which is what a listing taken now shows.
        Assert.Equal(clock.NowUnix, token.LastUsedAtUnix);
        // On disk it still says the moment of the last write-through.
        Assert.Equal(persisted, Reload(store, token).LastUsedAtUnix);
    }

    [Fact]
    public void TheStampIsWrittenThroughAgainOnceTheMinuteHasPassed()
    {
        var store = NewStore();
        var token = new ApiTokenService(store, clock).Create(Request()).Token!;
        store.RecordUse(token, clock.NowUnix);

        clock.Advance(TimeSpan.FromSeconds(ApiTokenStore.LastUsedWriteIntervalSeconds));
        store.RecordUse(token, clock.NowUnix);

        Assert.Equal(clock.NowUnix, Reload(store, token).LastUsedAtUnix);
    }

    [Fact]
    public void TheThrottleSurvivesARestartRatherThanRestarting()
    {
        var store = NewStore();
        var token = new ApiTokenService(store, clock).Create(Request()).Token!;
        store.RecordUse(token, clock.NowUnix);
        long persisted = clock.NowUnix;

        clock.Advance(TimeSpan.FromSeconds(5));
        var reloaded = NewStore();
        var held = reloaded.FindById(token.Id)!;
        reloaded.RecordUse(held, clock.NowUnix);

        // The file still says what it said: a restart every few seconds must not turn into a
        // file write every few seconds.
        Assert.Equal(persisted, Reload(reloaded, token).LastUsedAtUnix);
    }

    // ---- listing ----

    [Fact]
    public void TheListingIsOldestFirst()
    {
        var service = NewService();
        service.Create(Request("first"));
        clock.Advance(TimeSpan.FromMinutes(1));
        service.Create(Request("second"));

        Assert.Equal(new[] { "first", "second" }, service.List().ConvertAll(t => t.Name));
    }

    [Fact]
    public void ATokenIsFoundByIdWhateverTheCaseOfIt()
    {
        var store = NewStore();
        var token = new ApiTokenService(store, clock).Create(Request()).Token!;

        Assert.NotNull(store.FindById(token.Id.ToUpperInvariant()));
        Assert.Null(store.FindById(""));
        Assert.Null(store.FindByHash(""));
    }

    // ---- the JSON the file holds ----

    [Fact]
    public void TheFileIsTheSameEnvelopeTheOtherTwoListsUse()
    {
        NewService().Create(Request());

        using var doc = JsonDocument.Parse(File.ReadAllText(TokensPath));
        Assert.Equal(JsonValueKind.Array, doc.RootElement.GetProperty("entries").ValueKind);
        Assert.True(doc.RootElement.GetProperty("updatedAtUnix").GetInt64() > 0);
    }

    /// <summary>What the file says about this token, read through a fresh store, which is the
    /// only thing the next process can do. The store passed in is the one under test and is only
    /// there to make the reading order obvious at the call site.</summary>
    private ApiToken Reload(ApiTokenStore store, ApiToken token)
    {
        _ = store;
        return NewStore().FindById(token.Id)!;
    }
}
