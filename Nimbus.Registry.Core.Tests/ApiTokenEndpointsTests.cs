using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Nimbus.Registry.Services;
using Nimbus.Shared;
using Nimbus.Shared.Models;
using Nimbus.Shared.Security;
using Xunit;

namespace Nimbus.Registry.Core.Tests;

/// <summary>
/// The whole token path against a real registry: RegistryHosting, both middlewares in the order
/// they are registered, the endpoints, and the state files underneath. A bot author's two-line
/// integration is an HTTP request with one header on it, so that is what these tests send.
///
/// The connection is loopback plain HTTP, which is what the transport gate accepts and what a
/// test process can actually produce; the refusals that need another kind of connection are in
/// TokenAuthMiddlewareTests, where the connection is built rather than dialled.
/// </summary>
public class ApiTokenEndpointsTests
{
    private const string Secret = "token-endpoint-test-secret";
    private const string BotName = "discord-bot";

    private sealed class Host : IAsyncDisposable
    {
        public required WebApplication App { get; init; }
        public required string BaseUrl { get; init; }
        public required string StateDir { get; init; }
        public HttpClient Client { get; } = new();

        public static async Task<Host> StartAsync(bool tokensEnabled = true)
        {
            var cfg = new RegistryConfig { SharedSecret = Secret };
            cfg.ApiTokens.Enabled = tokensEnabled;
            // A directory per host, for the reason RegistryEndpointsTests gives: the state files
            // default to the working directory, and every host in this class would otherwise
            // share one set of them.
            cfg.StateDir = Path.Combine(Path.GetTempPath(), "nimbus-token-endpoint-tests-" + Guid.NewGuid().ToString("N"));
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.AddNimbusRegistry(cfg, withMasterServer: false);
            var app = builder.Build();
            app.UseNimbusRegistry();
            await app.StartAsync();
            return new Host { App = app, BaseUrl = app.Urls.First(), StateDir = cfg.StateDir };
        }

        public string TokensFile => Path.Combine(StateDir, RegistryStateFiles.TokensFileName);

        /// <summary>Mints a token the way an operator does, over the signed management endpoint,
        /// and hands back the plaintext plus the record.</summary>
        public async Task<(string Plaintext, ApiToken Record)> MintAsync(params string[] scopes)
        {
            var resp = await Client.SendAsync(Signed(HttpMethod.Post, "/api/tokens", new ApiTokenCreateRequest
            {
                Name = BotName,
                Scopes = (scopes.Length == 0 ? new[] { ApiTokenScopes.BansWrite } : scopes).ToList(),
                CreatedBy = "admin",
            }));
            resp.EnsureSuccessStatusCode();
            var parsed = await resp.Content.ReadFromJsonAsync<ApiTokenCreateResponse>();
            return (parsed!.Token, parsed.Record!);
        }

        /// <summary>A request carrying the bearer credential and nothing else. No signature, no
        /// nonce, no protocol header: the whole point is that a webhook author does not implement
        /// canonical-string signing.</summary>
        public HttpRequestMessage Bearer(HttpMethod method, string path, string token, object? body = null)
        {
            var msg = new HttpRequestMessage(method, BaseUrl.TrimEnd('/') + path);
            if (body is not null)
            {
                // A byte array rather than JsonContent, so the request carries a Content-Length.
                // /api/bans/lift and /api/whitelist/remove read a body that may legitimately be
                // absent and count an unknown length as absent, so a chunked request loses its
                // arguments. Every shipped client already sends a length, and so does curl.
                msg.Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(body));
                msg.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            }
            msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return msg;
        }

        public HttpRequestMessage Signed(HttpMethod method, string path, object? body = null)
            => SignedRaw(method, path, body is null ? null : JsonSerializer.SerializeToUtf8Bytes(body));

        /// <summary>Signs whatever bytes it is handed, valid JSON or not, so a handler can be
        /// reached with a body the signature check would otherwise never let through.</summary>
        public HttpRequestMessage SignedRaw(HttpMethod method, string path, byte[]? body)
        {
            byte[] bytes = body ?? Array.Empty<byte>();
            var msg = new HttpRequestMessage(method, BaseUrl.TrimEnd('/') + path);
            if (body is not null)
            {
                msg.Content = new ByteArrayContent(bytes);
                msg.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            }
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            string nonce = HmacSigner.NewNonce();
            msg.Headers.Add(NimbusProtocol.SignatureHeader, HmacSigner.Sign(Secret,
                HmacSigner.CanonicalString(method.Method, path, NimbusProtocol.ProtocolVersion, ts, nonce, bytes)));
            msg.Headers.Add(NimbusProtocol.TimestampHeader, ts.ToString());
            msg.Headers.Add(NimbusProtocol.NonceHeader, nonce);
            msg.Headers.Add(NimbusProtocol.ProtocolHeader, NimbusProtocol.ProtocolVersion.ToString());
            return msg;
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await App.StopAsync();
            await App.DisposeAsync();
            try { Directory.Delete(StateDir, recursive: true); } catch { /* never created */ }
        }
    }

    // ---- management, over HMAC ----

    [Fact]
    public async Task CreatingATokenAnswersThePlaintextOnceAndTheRecordWithoutIt()
    {
        await using var host = await Host.StartAsync();

        var resp = await host.Client.SendAsync(host.Signed(HttpMethod.Post, "/api/tokens", new ApiTokenCreateRequest
        {
            Name = BotName,
            Scopes = new List<string> { ApiTokenScopes.WhitelistWrite },
            CreatedBy = "admin",
        }));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var parsed = await resp.Content.ReadFromJsonAsync<ApiTokenCreateResponse>();
        Assert.True(parsed!.Ok);
        Assert.StartsWith(ApiTokenSecret.Prefix, parsed.Token);
        Assert.Equal(BotName, parsed.Record!.Name);
        // The record next to the secret is redacted like every other listing.
        Assert.Equal("", parsed.Record.Hash);
    }

    [Fact]
    public async Task TheSecretIsNeverInTheListingAndNeverOnDisk()
    {
        await using var host = await Host.StartAsync();
        var (plaintext, record) = await host.MintAsync();

        var resp = await host.Client.SendAsync(host.Signed(HttpMethod.Get, "/api/tokens"));
        string listingJson = await resp.Content.ReadAsStringAsync();
        string fileJson = await File.ReadAllTextAsync(host.TokensFile);

        Assert.DoesNotContain(plaintext, listingJson, StringComparison.Ordinal);
        Assert.DoesNotContain(plaintext, fileJson, StringComparison.Ordinal);
        var listed = await resp.Content.ReadFromJsonAsync<ApiTokenListResponse>();
        Assert.Equal(record.Id, Assert.Single(listed!.Tokens).Id);
        Assert.Equal("", listed.Tokens[0].Hash);
    }

    [Fact]
    public async Task CreatingATokenWithoutANameIs400()
    {
        await using var host = await Host.StartAsync();

        var resp = await host.Client.SendAsync(host.Signed(HttpMethod.Post, "/api/tokens",
            new ApiTokenCreateRequest { Scopes = new List<string> { ApiTokenScopes.BansRead } }));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("name required", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task CreatingATokenWithNoScopesIs400()
    {
        await using var host = await Host.StartAsync();

        var resp = await host.Client.SendAsync(host.Signed(HttpMethod.Post, "/api/tokens",
            new ApiTokenCreateRequest { Name = BotName }));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("scope", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AnUnknownScopeIs400AndNamesTheVocabulary()
    {
        await using var host = await Host.StartAsync();

        var resp = await host.Client.SendAsync(host.Signed(HttpMethod.Post, "/api/tokens",
            new ApiTokenCreateRequest { Name = BotName, Scopes = new List<string> { "bans:destroy" } }));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        string body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("bans:destroy", body);
        Assert.Contains(ApiTokenScopes.WhitelistWrite, body);
    }

    [Theory]
    [InlineData("/api/tokens")]
    [InlineData("/api/tokens/revoke")]
    public async Task AMalformedBodyIs400(string path)
    {
        await using var host = await Host.StartAsync();

        // Signed over the broken bytes, so this reaches the handler rather than being turned away
        // by the signature check: what is under test is the handler's own reading of the body.
        var resp = await host.Client.SendAsync(host.SignedRaw(HttpMethod.Post, path, "{not json"u8.ToArray()));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("malformed body", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task RevokingByIdIs200AndRevokingAgainIs404()
    {
        await using var host = await Host.StartAsync();
        var (_, record) = await host.MintAsync();

        var first = await host.Client.SendAsync(host.Signed(HttpMethod.Post, "/api/tokens/revoke",
            new ApiTokenRevokeRequest { Id = record.Id }));
        var second = await host.Client.SendAsync(host.Signed(HttpMethod.Post, "/api/tokens/revoke",
            new ApiTokenRevokeRequest { Id = record.Id }));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, second.StatusCode);
    }

    [Fact]
    public async Task RevokingWithoutAnIdIs400()
    {
        await using var host = await Host.StartAsync();

        var resp = await host.Client.SendAsync(host.Signed(HttpMethod.Post, "/api/tokens/revoke",
            new ApiTokenRevokeRequest()));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task TheManagementEndpointsStillNeedASignature()
    {
        await using var host = await Host.StartAsync();

        var resp = await host.Client.GetAsync(host.BaseUrl + "/api/tokens");

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Contains("missing nimbus headers", await resp.Content.ReadAsStringAsync());
    }

    // ---- authenticating with one ----

    [Fact]
    public async Task ABearerTokenWithTheRightScopeWritesABan()
    {
        await using var host = await Host.StartAsync();
        var (plaintext, _) = await host.MintAsync(ApiTokenScopes.BansWrite);

        var resp = await host.Client.SendAsync(host.Bearer(HttpMethod.Post, "/api/bans", plaintext,
            new BanRequest { PlayerUid = "uid-1", PlayerName = "griefer", Reason = "spam" }));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var parsed = await resp.Content.ReadFromJsonAsync<BanResponse>();
        Assert.True(parsed!.Ok);
        Assert.Equal("uid-1", parsed.Ban!.PlayerUid);
    }

    [Fact]
    public async Task ABanWrittenWithATokenIsAttributedToIt()
    {
        await using var host = await Host.StartAsync();
        var (plaintext, _) = await host.MintAsync(ApiTokenScopes.BansWrite, ApiTokenScopes.BansRead);

        await host.Client.SendAsync(host.Bearer(HttpMethod.Post, "/api/bans", plaintext,
            new BanRequest { PlayerUid = "uid-1", Reason = "spam" }));

        var listed = await (await host.Client.SendAsync(host.Bearer(HttpMethod.Get, "/api/bans", plaintext)))
            .Content.ReadFromJsonAsync<BanListResponse>();
        Assert.Equal("token:" + BotName, Assert.Single(listed!.Bans).BannedBy);
    }

    [Fact]
    public async Task ATokenCannotSignSomebodyElsesNameToABan()
    {
        await using var host = await Host.StartAsync();
        var (plaintext, _) = await host.MintAsync(ApiTokenScopes.BansWrite, ApiTokenScopes.BansRead);

        await host.Client.SendAsync(host.Bearer(HttpMethod.Post, "/api/bans", plaintext,
            new BanRequest { PlayerUid = "uid-1", BannedBy = "console" }));

        // Overwritten, not defaulted: the credential names its holder, and a caller filling in
        // the field must not be able to point the audit trail at somebody else.
        var listed = await (await host.Client.SendAsync(host.Bearer(HttpMethod.Get, "/api/bans", plaintext)))
            .Content.ReadFromJsonAsync<BanListResponse>();
        Assert.Equal("token:" + BotName, Assert.Single(listed!.Bans).BannedBy);
    }

    [Fact]
    public async Task AWhitelistEntryWrittenWithATokenIsAttributedToIt()
    {
        await using var host = await Host.StartAsync();
        var (plaintext, _) = await host.MintAsync(ApiTokenScopes.WhitelistWrite, ApiTokenScopes.WhitelistRead);

        await host.Client.SendAsync(host.Bearer(HttpMethod.Post, "/api/whitelist", plaintext,
            new WhitelistRequest { PlayerUid = "uid-1", Note = "closed beta", AddedBy = "console" }));

        var listed = await (await host.Client.SendAsync(host.Bearer(HttpMethod.Get, "/api/whitelist", plaintext)))
            .Content.ReadFromJsonAsync<WhitelistListResponse>();
        Assert.Equal("token:" + BotName, Assert.Single(listed!.Entries).AddedBy);
    }

    [Fact]
    public async Task AnHmacCallKeepsAttributingItself()
    {
        await using var host = await Host.StartAsync();

        await host.Client.SendAsync(host.Signed(HttpMethod.Post, "/api/bans",
            new BanRequest { PlayerUid = "uid-1", BannedBy = "console" }));

        var listed = await (await host.Client.SendAsync(host.Signed(HttpMethod.Get, "/api/bans")))
            .Content.ReadFromJsonAsync<BanListResponse>();
        Assert.Equal("console", Assert.Single(listed!.Bans).BannedBy);
    }

    [Fact]
    public async Task ABearerTokenCanReadTheNetworkSnapshot()
    {
        await using var host = await Host.StartAsync();
        var (plaintext, _) = await host.MintAsync(ApiTokenScopes.ServersRead);

        var resp = await host.Client.SendAsync(host.Bearer(HttpMethod.Get, "/api/servers", plaintext));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.NotNull(await resp.Content.ReadFromJsonAsync<NetworkSnapshot>());
    }

    [Fact]
    public async Task ABearerTokenCanLiftABanItPlaced()
    {
        await using var host = await Host.StartAsync();
        var (plaintext, _) = await host.MintAsync(ApiTokenScopes.BansWrite);
        await host.Client.SendAsync(host.Bearer(HttpMethod.Post, "/api/bans", plaintext,
            new BanRequest { PlayerUid = "uid-1" }));

        var resp = await host.Client.SendAsync(host.Bearer(HttpMethod.Post, "/api/bans/lift", plaintext,
            new BanLiftRequest { PlayerUid = "uid-1" }));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task ABearerTokenCanRemoveAWhitelistEntry()
    {
        await using var host = await Host.StartAsync();
        var (plaintext, _) = await host.MintAsync(ApiTokenScopes.WhitelistWrite);
        await host.Client.SendAsync(host.Bearer(HttpMethod.Post, "/api/whitelist", plaintext,
            new WhitelistRequest { PlayerUid = "uid-1" }));

        var resp = await host.Client.SendAsync(host.Bearer(HttpMethod.Post, "/api/whitelist/remove", plaintext,
            new WhitelistRemoveRequest { PlayerUid = "uid-1" }));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    // ---- refusals, over the real pipeline ----

    [Fact]
    public async Task AnUnknownBearerTokenIs401()
    {
        await using var host = await Host.StartAsync();

        var resp = await host.Client.SendAsync(host.Bearer(HttpMethod.Get, "/api/bans", ApiTokenSecret.NewSecret()));

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Equal("unknown token", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ARevokedTokenStopsWorkingImmediately()
    {
        await using var host = await Host.StartAsync();
        var (plaintext, record) = await host.MintAsync(ApiTokenScopes.BansRead);
        Assert.Equal(HttpStatusCode.OK, (await host.Client.SendAsync(host.Bearer(HttpMethod.Get, "/api/bans", plaintext))).StatusCode);

        await host.Client.SendAsync(host.Signed(HttpMethod.Post, "/api/tokens/revoke",
            new ApiTokenRevokeRequest { Id = record.Id }));

        var resp = await host.Client.SendAsync(host.Bearer(HttpMethod.Get, "/api/bans", plaintext));
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Equal("token revoked", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ATokenWithoutTheScopeIs403()
    {
        await using var host = await Host.StartAsync();
        var (plaintext, _) = await host.MintAsync(ApiTokenScopes.WhitelistWrite);

        var resp = await host.Client.SendAsync(host.Bearer(HttpMethod.Post, "/api/bans", plaintext,
            new BanRequest { PlayerUid = "uid-1" }));

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        Assert.Contains(ApiTokenScopes.BansWrite, await resp.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("/api/heartbeat")]
    [InlineData("/api/reservations")]
    [InlineData("/api/transfer-intents/drain")]
    [InlineData("/api/tokens")]
    public async Task ABearerOnAnInternalEndpointIs403(string path)
    {
        await using var host = await Host.StartAsync();
        var (plaintext, _) = await host.MintAsync(ApiTokenScopes.All.ToArray());

        var resp = await host.Client.SendAsync(host.Bearer(HttpMethod.Post, path, plaintext, new { }));

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        Assert.Contains("HMAC", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ATokenCannotMintItselfABetterOne()
    {
        await using var host = await Host.StartAsync();
        var (plaintext, _) = await host.MintAsync(ApiTokenScopes.All.ToArray());

        var resp = await host.Client.SendAsync(host.Bearer(HttpMethod.Post, "/api/tokens", plaintext,
            new ApiTokenCreateRequest { Name = "escalated", Scopes = new List<string> { ApiTokenScopes.BansWrite } }));

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task BearerAuthIsRefusedWhileTheSwitchIsOff()
    {
        await using var host = await Host.StartAsync(tokensEnabled: false);
        var (plaintext, _) = await host.MintAsync(ApiTokenScopes.BansRead);

        var resp = await host.Client.SendAsync(host.Bearer(HttpMethod.Get, "/api/bans", plaintext));

        // Minting still works with the switch off, which is how an operator gets a bot ready
        // before turning it on. Authenticating does not.
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Contains("disabled", await resp.Content.ReadAsStringAsync());
    }

    // ---- the two credentials side by side ----

    [Fact]
    public async Task ASignedCallIsUnaffectedByAnyOfThis()
    {
        await using var host = await Host.StartAsync();

        var resp = await host.Client.SendAsync(host.Signed(HttpMethod.Get, "/api/servers"));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task ABearerHeaderThatIsNotOursFallsThroughToTheHmacRefusal()
    {
        await using var host = await Host.StartAsync();
        var msg = new HttpRequestMessage(HttpMethod.Get, host.BaseUrl + "/api/servers");
        msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "somebody-elses-jwt");

        var resp = await host.Client.SendAsync(msg);

        // Not our credential, so it is left alone and the request is judged the way it always was.
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Contains("missing nimbus headers", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task TheUnauthenticatedRoutesStayUnauthenticated()
    {
        await using var host = await Host.StartAsync();

        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync(host.BaseUrl + "/health")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync(host.BaseUrl + "/")).StatusCode);
    }

    // ---- across a restart ----

    [Fact]
    public async Task ATokenAndItsRevocationBothOutliveTheRegistry()
    {
        await using var host = await Host.StartAsync();
        var (plaintext, record) = await host.MintAsync(ApiTokenScopes.BansRead);
        var (survivorPlaintext, _) = await host.MintAsync(ApiTokenScopes.BansRead);
        await host.Client.SendAsync(host.Signed(HttpMethod.Post, "/api/tokens/revoke",
            new ApiTokenRevokeRequest { Id = record.Id }));

        // The next process reads the same directory, which is the only thing a restart is.
        var reloaded = new ApiTokenStore(TimeProvider.System, RegistryStateFiles.Tokens(host.StateDir));

        Assert.True(reloaded.FindByHash(ApiTokenSecret.Hash(plaintext))!.Revoked);
        Assert.False(reloaded.FindByHash(ApiTokenSecret.Hash(survivorPlaintext))!.Revoked);
    }

    [Fact]
    public async Task TheLastUseIsRecorded()
    {
        await using var host = await Host.StartAsync();
        var (plaintext, record) = await host.MintAsync(ApiTokenScopes.BansRead);
        Assert.Equal(0, record.LastUsedAtUnix);

        await host.Client.SendAsync(host.Bearer(HttpMethod.Get, "/api/bans", plaintext));

        var listed = await (await host.Client.SendAsync(host.Signed(HttpMethod.Get, "/api/tokens")))
            .Content.ReadFromJsonAsync<ApiTokenListResponse>();
        Assert.True(Assert.Single(listed!.Tokens).LastUsedAtUnix > 0);
    }
}
