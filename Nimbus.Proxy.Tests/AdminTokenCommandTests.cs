using System.Text.Json;
using Nimbus.Registry.Services;
using Nimbus.Shared.Models;
using Xunit;

namespace Nimbus.Proxy.Tests;

/// <summary>
/// The token verbs over the admin socket, which is the only path an operator has to them: the
/// registry's /api/tokens endpoints take HMAC and nothing else, so the proxy reaches them on the
/// operator's behalf and the plaintext travels back through that chain exactly once.
///
/// The thing worth pinning here is what the create reply carries. A secret shown once and not
/// written down has to be reissued, so the warning is a field in the response rather than a habit
/// on the operator's part.
/// </summary>
public class AdminTokenCommandTests
{
    private const string BotName = "discord-bot";

    private static ApiToken Record(string id = "a1b2c3", string name = BotName, bool revoked = false,
        long expiresAtUnix = 0, long lastUsedAtUnix = 0) => new()
    {
        Id = id,
        Name = name,
        Scopes = new List<string> { ApiTokenScopes.WhitelistWrite },
        CreatedBy = "admin",
        CreatedAtUnix = 1_780_000_000,
        ExpiresAtUnix = expiresAtUnix,
        LastUsedAtUnix = lastUsedAtUnix,
        Revoked = revoked,
    };

    // ---- create ----

    [Fact]
    public async Task ACreate_AnswersTheSecretOnceAndSaysSo()
    {
        await using var harness = await AdminHarness.StartAsync();
        harness.Registry.CreateApiTokenResult = new ApiTokenCreateResponse
        {
            Ok = true,
            Token = "nsk_" + new string('a', 43),
            Record = Record(),
        };

        var reply = await harness.RunAsync(new
        {
            cmd = "token-create",
            name = BotName,
            scopes = "whitelist:write",
        });

        Assert.True(reply.GetProperty("ok").GetBoolean());
        Assert.Equal("a1b2c3", reply.GetProperty("id").GetString());
        Assert.Equal(BotName, reply.GetProperty("name").GetString());
        Assert.StartsWith("nsk_", reply.GetProperty("token").GetString());
        Assert.Equal(TokenCreateCommand.ShownOnceWarning, reply.GetProperty("warning").GetString());
    }

    [Fact]
    public async Task ACreate_SplitsTheScopeListAndStampsWhoAskedForIt()
    {
        await using var harness = await AdminHarness.StartAsync();
        harness.Registry.CreateApiTokenResult = new ApiTokenCreateResponse
        { Ok = true, Token = "nsk_x", Record = Record() };

        await harness.RunAsync(new
        {
            cmd = "token-create",
            name = BotName,
            scopes = "bans:write, whitelist:write ,",
            duration = 86400,
        });

        var recorded = Assert.IsType<ApiTokenCreateRequest>(harness.Registry.LastApiTokenRequest);
        Assert.Equal(BotName, recorded.Name);
        Assert.Equal(new[] { "bans:write", "whitelist:write" }, recorded.Scopes);
        Assert.Equal(86400, recorded.DurationSeconds);
        Assert.False(recorded.Permanent);
        Assert.Equal("admin", recorded.CreatedBy);
    }

    [Fact]
    public async Task ACreate_CarriesPermanenceThrough()
    {
        await using var harness = await AdminHarness.StartAsync();
        harness.Registry.CreateApiTokenResult = new ApiTokenCreateResponse
        { Ok = true, Token = "nsk_x", Record = Record() };

        await harness.RunAsync(new { cmd = "token-create", name = BotName, scopes = "servers:read", permanent = true });

        Assert.True(harness.Registry.LastApiTokenRequest!.Permanent);
    }

    [Fact]
    public async Task ACreate_WithoutANameIsRefusedBeforeItReachesTheRegistry()
    {
        await using var harness = await AdminHarness.StartAsync();

        var reply = await harness.RunAsync(new { cmd = "token-create", scopes = "bans:read" });

        Assert.False(reply.GetProperty("ok").GetBoolean());
        Assert.Contains("name", reply.GetProperty("reason").GetString());
        Assert.Null(harness.Registry.LastApiTokenRequest);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" , , ")]
    public async Task ACreate_WithNoUsableScopesNamesTheVocabulary(string scopes)
    {
        await using var harness = await AdminHarness.StartAsync();

        var reply = await harness.RunAsync(new { cmd = "token-create", name = BotName, scopes });

        Assert.False(reply.GetProperty("ok").GetBoolean());
        Assert.Contains("whitelist:write", reply.GetProperty("reason").GetString());
        Assert.Null(harness.Registry.LastApiTokenRequest);
    }

    [Fact]
    public async Task ACreate_TheRegistryRefusedSaysSoWithoutInventingAToken()
    {
        await using var harness = await AdminHarness.StartAsync();
        harness.Registry.CreateApiTokenResult = null;

        var reply = await harness.RunAsync(new { cmd = "token-create", name = BotName, scopes = "bans:read" });

        Assert.False(reply.GetProperty("ok").GetBoolean());
        Assert.False(reply.TryGetProperty("token", out _));
    }

    [Fact]
    public async Task ACreate_AnEmptySecretIsTreatedAsARefusal()
    {
        await using var harness = await AdminHarness.StartAsync();
        // A response that carries a record and no secret is a registry that answered something
        // this command cannot hand to an operator.
        harness.Registry.CreateApiTokenResult = new ApiTokenCreateResponse { Ok = true, Token = "", Record = Record() };

        var reply = await harness.RunAsync(new { cmd = "token-create", name = BotName, scopes = "bans:read" });

        Assert.False(reply.GetProperty("ok").GetBoolean());
    }

    // ---- revoke ----

    [Fact]
    public async Task ARevoke_PassesTheIdAndReportsTheOutcome()
    {
        await using var harness = await AdminHarness.StartAsync();
        harness.Registry.RevokeApiTokenResult = true;

        var reply = await harness.RunAsync(new { cmd = "token-revoke", id = "a1b2c3" });

        Assert.True(reply.GetProperty("ok").GetBoolean());
        Assert.Equal("a1b2c3", harness.Registry.LastApiTokenRevoke);
    }

    [Fact]
    public async Task ARevoke_ThatMatchedNothingIsNotAnOk()
    {
        await using var harness = await AdminHarness.StartAsync();
        harness.Registry.RevokeApiTokenResult = false;

        var reply = await harness.RunAsync(new { cmd = "token-revoke", id = "nope" });

        Assert.False(reply.GetProperty("ok").GetBoolean());
        Assert.Contains("already revoked", reply.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task ARevoke_WithNoIdIsRefused()
    {
        await using var harness = await AdminHarness.StartAsync();

        var reply = await harness.RunAsync(new { cmd = "token-revoke" });

        Assert.False(reply.GetProperty("ok").GetBoolean());
        Assert.Null(harness.Registry.LastApiTokenRevoke);
    }

    // ---- list ----

    [Fact]
    public async Task AListing_SaysWhichTokensStillAuthenticate()
    {
        await using var harness = await AdminHarness.StartAsync();
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        harness.Registry.ApiTokens = new List<ApiToken>
        {
            Record("live", "live-bot", expiresAtUnix: now + 3600, lastUsedAtUnix: now - 5),
            Record("gone", "revoked-bot", revoked: true),
            Record("old", "expired-bot", expiresAtUnix: now - 1),
        };

        var reply = await harness.RunAsync(new { cmd = "token-list" });

        Assert.True(reply.GetProperty("ok").GetBoolean());
        Assert.Equal(3, reply.GetProperty("count").GetInt32());
        var tokens = reply.GetProperty("tokens").EnumerateArray().ToList();
        // All three are listed: the record is what an operator reads after a leak, and an absence
        // answers none of the questions they are asking.
        Assert.True(tokens[0].GetProperty("usable").GetBoolean());
        Assert.False(tokens[1].GetProperty("usable").GetBoolean());
        Assert.True(tokens[1].GetProperty("revoked").GetBoolean());
        Assert.False(tokens[2].GetProperty("usable").GetBoolean());
    }

    [Fact]
    public async Task AListing_NeverCarriesAHash()
    {
        await using var harness = await AdminHarness.StartAsync();
        harness.Registry.ApiTokens = new List<ApiToken> { Record() with { Hash = "deadbeef" } };

        var reply = await harness.RunAsync(new { cmd = "token-list" });

        Assert.DoesNotContain("deadbeef", reply.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.False(reply.GetProperty("tokens")[0].TryGetProperty("hash", out _));
    }

    [Fact]
    public async Task AListing_ARegistryThatCannotAnswerIsNotAnEmptyList()
    {
        await using var harness = await AdminHarness.StartAsync();
        harness.Registry.ApiTokens = null;

        var reply = await harness.RunAsync(new { cmd = "token-list" });

        Assert.False(reply.GetProperty("ok").GetBoolean());
        Assert.Contains("unreachable", reply.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task AListing_AnswersUnderItsAliasToo()
    {
        await using var harness = await AdminHarness.StartAsync();
        harness.Registry.ApiTokens = new List<ApiToken>();

        var reply = await harness.RunAsync(new { cmd = "tokens" });

        Assert.True(reply.GetProperty("ok").GetBoolean());
    }

    // ---- no registry ----

    [Theory]
    [InlineData("token-list")]
    [InlineData("token-revoke")]
    [InlineData("token-create")]
    public async Task WithoutARegistry_EveryVerbSaysWhyRatherThanFailing(string cmd)
    {
        await using var harness = await AdminHarness.StartAsync(withRegistry: false);

        var reply = await harness.RunAsync(JsonSerializer.Serialize(new
        {
            cmd,
            name = BotName,
            scopes = "bans:read",
            id = "a1b2c3",
        }));

        Assert.False(reply.GetProperty("ok").GetBoolean());
        Assert.Contains("registry.mode is 'disabled'", reply.GetProperty("reason").GetString());
    }

    // ---- permissions ----

    [Theory]
    [InlineData("token-create", "nimbus.command.token.create")]
    [InlineData("token-revoke", "nimbus.command.token.revoke")]
    [InlineData("token-list", "nimbus.command.token.list")]
    public async Task EveryVerbSitsUnderItsOwnPermission(string cmd, string permission)
    {
        await using var harness = await AdminHarness.StartAsync(
            configure: cfg => cfg.Admin.GrantedPermissions = new List<string> { "nimbus.command.ping" });

        var reply = await harness.RunAsync(new { cmd });

        Assert.False(reply.GetProperty("ok").GetBoolean());
        Assert.Equal(permission, reply.GetProperty("permission").GetString());
    }

    [Fact]
    public async Task TheThreeVerbsAnnounceThemselvesInHelp()
    {
        await using var harness = await AdminHarness.StartAsync();

        var reply = await harness.RunAsync(new { cmd = "help" });

        var listed = reply.GetProperty("commands").EnumerateArray()
            .ToDictionary(c => c.GetProperty("name").GetString()!, c => c);
        foreach (var name in new[] { "token-create", "token-revoke", "token-list" })
        {
            Assert.True(listed.ContainsKey(name), $"{name} is missing from help");
            Assert.False(string.IsNullOrWhiteSpace(listed[name].GetProperty("summary").GetString()));
            Assert.StartsWith(name, listed[name].GetProperty("usage").GetString());
        }
        Assert.Equal("tokens", Assert.Single(listed["token-list"].GetProperty("aliases").EnumerateArray()).GetString());
    }

    [Fact]
    public async Task TheTokenPermissionsAreGrantableAsAGroup()
    {
        await using var harness = await AdminHarness.StartAsync(
            configure: cfg => cfg.Admin.GrantedPermissions = new List<string> { "nimbus.command.token.*" });
        harness.Registry.ApiTokens = new List<ApiToken>();

        var reply = await harness.RunAsync(new { cmd = "token-list" });

        Assert.True(reply.GetProperty("ok").GetBoolean());
    }
}
