using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Nimbus.Registry.Services;
using Nimbus.Shared.Models;
using Xunit;

namespace Nimbus.Registry.Core.Tests;

/// <summary>
/// Every outcome the token auth path can reach, driven through the middleware directly. The
/// endpoint tests boot a real registry over loopback HTTP, which is the right test for the
/// wiring but can only ever produce one transport: a loopback peer on plain HTTP. The refusals
/// that matter most are the other ones, so the connection is built here instead of dialled.
///
/// The logger is a recorder rather than a null sink, because "the log line never carries the
/// secret" is a requirement and not a hope.
/// </summary>
public class TokenAuthMiddlewareTests
{
    private const string BotName = "discord-bot";

    private sealed class RecordingLogger : ILogger<TokenAuthMiddleware>
    {
        public List<string> Lines { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex,
            Func<TState, Exception?, string> formatter)
            => Lines.Add(formatter(state, ex));

        public string Text => string.Join("\n", Lines);
    }

    /// <summary>The middleware, the store behind it and the request being handed to it. Nothing
    /// is a double: the store is the real one and the token is a real mint.</summary>
    private sealed class Harness
    {
        public required RegistryConfig Cfg { get; init; }
        public required ApiTokenStore Store { get; init; }
        public required ApiTokenService Service { get; init; }
        public required FakeClock Clock { get; init; }
        public required RecordingLogger Log { get; init; }

        /// <summary>Assigned after construction because the delegate behind it closes over this
        /// harness, which cannot exist before the harness does.</summary>
        public TokenAuthMiddleware Middleware { get; private set; } = null!;

        /// <summary>True once the request reached what sits behind the middleware, which for a
        /// bearer request means it was accepted and for anything else means it was left alone.</summary>
        public bool ReachedNext { get; set; }

        /// <summary>The context the accepted request carried, so the identity stamped on it can
        /// be read back.</summary>
        public HttpContext? Passed { get; set; }

        public static Harness Create(Action<ApiTokensConfig>? configure = null, int rateLimit = 60)
        {
            var clock = new FakeClock();
            var cfg = new RegistryConfig();
            cfg.ApiTokens.Enabled = true;
            cfg.ApiTokens.RateLimitPerMinute = rateLimit;
            configure?.Invoke(cfg.ApiTokens);

            var store = new ApiTokenStore(clock);
            var log = new RecordingLogger();
            var harness = new Harness
            {
                Cfg = cfg,
                Store = store,
                Service = new ApiTokenService(store, clock),
                Clock = clock,
                Log = log,
            };

            harness.Middleware = new TokenAuthMiddleware(
                ctx => { harness.ReachedNext = true; harness.Passed = ctx; return Task.CompletedTask; },
                cfg, store, new ApiTokenRateLimiter(cfg, clock), clock, log);
            return harness;
        }

        public (string Plaintext, ApiToken Token) Mint(params string[] scopes)
        {
            var result = Service.Create(new ApiTokenCreateRequest
            {
                Name = BotName,
                Scopes = (scopes.Length == 0 ? new[] { ApiTokenScopes.BansWrite } : scopes).ToList(),
                CreatedBy = "admin",
            });
            return (result.Plaintext, result.Token!);
        }

        public async Task<HttpContext> Send(string? bearer, string method = "POST", string path = "/api/bans",
            string remoteIp = "127.0.0.1", bool https = false, string? forwardedProto = null)
        {
            var ctx = new DefaultHttpContext();
            ctx.Request.Method = method;
            ctx.Request.Path = path;
            ctx.Request.Scheme = https ? "https" : "http";
            ctx.Connection.RemoteIpAddress = remoteIp.Length == 0 ? null : IPAddress.Parse(remoteIp);
            if (bearer is not null) ctx.Request.Headers.Authorization = bearer;
            if (forwardedProto is not null) ctx.Request.Headers["X-Forwarded-Proto"] = forwardedProto;
            ctx.Response.Body = new MemoryStream();

            await Middleware.Invoke(ctx);
            return ctx;
        }

        public static string BodyOf(HttpContext ctx)
        {
            ctx.Response.Body.Position = 0;
            return new StreamReader(ctx.Response.Body, Encoding.UTF8).ReadToEnd();
        }
    }

    // ---- falling through ----

    [Fact]
    public async Task ARequestWithNoAuthorizationHeaderIsNotTouched()
    {
        var h = Harness.Create();

        var ctx = await h.Send(bearer: null);

        // Untouched is the whole compatibility story: every backend, proxy and nimctl in
        // existence carries no bearer header and reaches HmacAuthMiddleware exactly as before,
        // which is why the protocol version does not move.
        Assert.True(h.ReachedNext);
        Assert.Null(ApiTokenIdentity.Of(ctx));
    }

    [Theory]
    [InlineData("Basic dXNlcjpwYXNz")]
    [InlineData("Bearer some-other-systems-jwt")]
    [InlineData("Bearer ")]
    [InlineData("nsk_notevenascheme")]
    public async Task ACredentialThatIsNotOursIsLeftAlone(string header)
    {
        var h = Harness.Create();

        await h.Send(header);

        Assert.True(h.ReachedNext);
    }

    [Fact]
    public async Task ABearerOutsideApiIsLeftAlone()
    {
        var h = Harness.Create();
        var (plaintext, _) = h.Mint();

        await h.Send("Bearer " + plaintext, method: "GET", path: "/health");

        Assert.True(h.ReachedNext);
    }

    // ---- the master switch ----

    [Fact]
    public async Task AValidTokenIsRefusedWhileTheSwitchIsOff()
    {
        var h = Harness.Create(cfg => cfg.Enabled = false);
        var (plaintext, _) = h.Mint();

        var ctx = await h.Send("Bearer " + plaintext);

        Assert.False(h.ReachedNext);
        Assert.Equal(StatusCodes.Status401Unauthorized, ctx.Response.StatusCode);
        Assert.Contains("disabled", Harness.BodyOf(ctx));
    }

    [Fact]
    public void TheSwitchIsOffByDefault()
        => Assert.False(new RegistryConfig().ApiTokens.Enabled);

    // ---- the transport gate ----

    [Fact]
    public async Task ALoopbackPeerOnPlainHttpIsAccepted()
    {
        var h = Harness.Create();
        var (plaintext, _) = h.Mint();

        await h.Send("Bearer " + plaintext);

        Assert.True(h.ReachedNext);
    }

    [Fact]
    public async Task AnIpv6LoopbackPeerIsAccepted()
    {
        var h = Harness.Create();
        var (plaintext, _) = h.Mint();

        await h.Send("Bearer " + plaintext, remoteIp: "::1");

        Assert.True(h.ReachedNext);
    }

    [Fact]
    public async Task AnIpv4MappedLoopbackPeerIsAccepted()
    {
        var h = Harness.Create();
        var (plaintext, _) = h.Mint();

        // What Kestrel reports on a dual-stack socket for a 127.0.0.1 client. IPAddress.IsLoopback
        // says false for this form, so the mapping has to be undone before the question is asked.
        await h.Send("Bearer " + plaintext, remoteIp: "::ffff:127.0.0.1");

        Assert.True(h.ReachedNext);
    }

    [Fact]
    public async Task AnInProcessCallerWithNoPeerAddressIsAccepted()
    {
        var h = Harness.Create();
        var (plaintext, _) = h.Mint();

        await h.Send("Bearer " + plaintext, remoteIp: "");

        Assert.True(h.ReachedNext);
    }

    [Fact]
    public async Task ARemotePeerOnPlainHttpIsRefused()
    {
        var h = Harness.Create();
        var (plaintext, _) = h.Mint();

        var ctx = await h.Send("Bearer " + plaintext, remoteIp: "203.0.113.9");

        // A bearer token is only as safe as the transport under it, and this one could be read
        // off the wire by anyone on the path.
        Assert.False(h.ReachedNext);
        Assert.Equal(StatusCodes.Status403Forbidden, ctx.Response.StatusCode);
        Assert.Contains("loopback", Harness.BodyOf(ctx));
    }

    [Fact]
    public async Task ARemotePeerOverTheRegistrysOwnTlsIsAccepted()
    {
        var h = Harness.Create();
        var (plaintext, _) = h.Mint();

        var ctx = await h.Send("Bearer " + plaintext, remoteIp: "203.0.113.9", https: true);

        Assert.True(h.ReachedNext);
        Assert.Equal(BotName, ApiTokenIdentity.Of(ctx)!.Name);
    }

    [Fact]
    public async Task AForwardedProtoHeaderIsIgnoredByDefault()
    {
        var h = Harness.Create();
        var (plaintext, _) = h.Mint();

        var ctx = await h.Send("Bearer " + plaintext, remoteIp: "203.0.113.9", forwardedProto: "https");

        // Anything that can reach the bind can write this header, so believing it by default
        // would make the gate a formality.
        Assert.False(h.ReachedNext);
        Assert.Equal(StatusCodes.Status403Forbidden, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task AForwardedProtoHeaderIsHonouredOnceItIsTrusted()
    {
        var h = Harness.Create(cfg => cfg.TrustForwardedProto = true);
        var (plaintext, _) = h.Mint();

        await h.Send("Bearer " + plaintext, remoteIp: "203.0.113.9", forwardedProto: "https");

        Assert.True(h.ReachedNext);
    }

    [Fact]
    public async Task TheFirstHopOfAForwardedProtoChainDecides()
    {
        var h = Harness.Create(cfg => cfg.TrustForwardedProto = true);
        var (plaintext, _) = h.Mint();

        // A proxy chain appends, so the entry describing the client's own connection is the
        // leftmost one. "http, https" means the client spoke plain HTTP to the first hop.
        var refused = await h.Send("Bearer " + plaintext, remoteIp: "203.0.113.9", forwardedProto: "http, https");
        Assert.False(h.ReachedNext);
        Assert.Equal(StatusCodes.Status403Forbidden, refused.Response.StatusCode);

        await h.Send("Bearer " + plaintext, remoteIp: "203.0.113.9", forwardedProto: "https, http");
        Assert.True(h.ReachedNext);
    }

    [Fact]
    public async Task ATrustedForwardedProtoOfPlainHttpIsStillRefused()
    {
        var h = Harness.Create(cfg => cfg.TrustForwardedProto = true);
        var (plaintext, _) = h.Mint();

        var ctx = await h.Send("Bearer " + plaintext, remoteIp: "203.0.113.9", forwardedProto: "http");

        Assert.False(h.ReachedNext);
        Assert.Equal(StatusCodes.Status403Forbidden, ctx.Response.StatusCode);
    }

    // ---- the credential ----

    [Fact]
    public async Task AnUnknownTokenIs401()
    {
        var h = Harness.Create();
        h.Mint();

        var ctx = await h.Send("Bearer " + ApiTokenSecret.NewSecret());

        Assert.False(h.ReachedNext);
        Assert.Equal(StatusCodes.Status401Unauthorized, ctx.Response.StatusCode);
        Assert.Equal("unknown token", Harness.BodyOf(ctx));
    }

    [Fact]
    public async Task ARevokedTokenIs401()
    {
        var h = Harness.Create();
        var (plaintext, token) = h.Mint();
        Assert.True(h.Service.Revoke(token.Id));

        var ctx = await h.Send("Bearer " + plaintext);

        Assert.False(h.ReachedNext);
        Assert.Equal(StatusCodes.Status401Unauthorized, ctx.Response.StatusCode);
        Assert.Equal("token revoked", Harness.BodyOf(ctx));
    }

    [Fact]
    public async Task AnExpiredTokenIs401()
    {
        var h = Harness.Create();
        var (plaintext, _) = h.Mint();
        h.Clock.Advance(TimeSpan.FromSeconds(ApiTokenService.DefaultDurationSeconds + 1));

        var ctx = await h.Send("Bearer " + plaintext);

        Assert.False(h.ReachedNext);
        Assert.Equal(StatusCodes.Status401Unauthorized, ctx.Response.StatusCode);
        Assert.Equal("token expired", Harness.BodyOf(ctx));
    }

    [Fact]
    public async Task ATokenIsStillGoodTheSecondBeforeItExpires()
    {
        var h = Harness.Create();
        var (plaintext, token) = h.Mint();
        h.Clock.Advance(TimeSpan.FromSeconds(ApiTokenService.DefaultDurationSeconds - 1));

        await h.Send("Bearer " + plaintext);

        Assert.True(h.ReachedNext);
        Assert.Equal(h.Clock.NowUnix, token.LastUsedAtUnix);
    }

    // ---- scopes ----

    [Fact]
    public async Task ATokenWithoutTheScopeForThisRouteIs403()
    {
        var h = Harness.Create();
        var (plaintext, _) = h.Mint(ApiTokenScopes.WhitelistWrite);

        var ctx = await h.Send("Bearer " + plaintext, "POST", "/api/bans");

        Assert.False(h.ReachedNext);
        Assert.Equal(StatusCodes.Status403Forbidden, ctx.Response.StatusCode);
        Assert.Contains("bans:write", Harness.BodyOf(ctx));
    }

    [Fact]
    public async Task AReadScopeDoesNotBuyAWrite()
    {
        var h = Harness.Create();
        var (plaintext, _) = h.Mint(ApiTokenScopes.BansRead);

        Assert.Equal(StatusCodes.Status403Forbidden,
            (await h.Send("Bearer " + plaintext, "POST", "/api/bans")).Response.StatusCode);
        h.ReachedNext = false;

        await h.Send("Bearer " + plaintext, "GET", "/api/bans");
        Assert.True(h.ReachedNext);
    }

    [Theory]
    [InlineData("GET", "/api/bans", ApiTokenScopes.BansRead)]
    [InlineData("POST", "/api/bans", ApiTokenScopes.BansWrite)]
    [InlineData("POST", "/api/bans/lift", ApiTokenScopes.BansWrite)]
    [InlineData("GET", "/api/whitelist", ApiTokenScopes.WhitelistRead)]
    [InlineData("POST", "/api/whitelist", ApiTokenScopes.WhitelistWrite)]
    [InlineData("POST", "/api/whitelist/remove", ApiTokenScopes.WhitelistWrite)]
    [InlineData("GET", "/api/servers", ApiTokenScopes.ServersRead)]
    public async Task EveryScopedRouteAcceptsTheScopeItDeclares(string method, string path, string scope)
    {
        var h = Harness.Create();
        var (plaintext, _) = h.Mint(scope);

        await h.Send("Bearer " + plaintext, method, path);

        Assert.True(h.ReachedNext);
    }

    [Fact]
    public async Task ATrailingSlashDoesNotChangeWhatARouteCosts()
    {
        var h = Harness.Create();
        var (plaintext, _) = h.Mint(ApiTokenScopes.BansWrite);

        await h.Send("Bearer " + plaintext, "POST", "/api/bans/");

        Assert.True(h.ReachedNext);
    }

    // ---- the internal endpoints ----

    [Theory]
    [InlineData("POST", "/api/heartbeat")]
    [InlineData("POST", "/api/reservations")]
    [InlineData("POST", "/api/reservations/abc/consume")]
    [InlineData("POST", "/api/reservations/consume-by-uid")]
    [InlineData("POST", "/api/transfer-intents")]
    [InlineData("POST", "/api/transfer-intents/drain")]
    [InlineData("POST", "/api/tokens")]
    [InlineData("POST", "/api/tokens/revoke")]
    [InlineData("GET", "/api/tokens")]
    [InlineData("GET", "/api/something-added-later")]
    public async Task NoScopeReachesAnInternalEndpoint(string method, string path)
    {
        var h = Harness.Create();
        // Every scope there is, which is the strongest token that can exist.
        var (plaintext, _) = h.Mint(ApiTokenScopes.All.ToArray());

        var ctx = await h.Send("Bearer " + plaintext, method, path);

        Assert.False(h.ReachedNext);
        Assert.Equal(StatusCodes.Status403Forbidden, ctx.Response.StatusCode);
        Assert.Contains("HMAC", Harness.BodyOf(ctx));
    }

    [Fact]
    public async Task AnInternalEndpointRefusesBeforeItLooksAtTheCredential()
    {
        var h = Harness.Create();

        // Garbage, and the answer is the same 403 a perfectly good token gets. Nothing about the
        // credential is readable off the reply.
        var ctx = await h.Send("Bearer " + ApiTokenSecret.NewSecret(), "POST", "/api/heartbeat");

        Assert.Equal(StatusCodes.Status403Forbidden, ctx.Response.StatusCode);
        Assert.Contains("HMAC", Harness.BodyOf(ctx));
    }

    // ---- rate limiting ----

    [Fact]
    public async Task TheRateLimitAnswers429WithARetryAfter()
    {
        var h = Harness.Create(rateLimit: 2);
        var (plaintext, _) = h.Mint();

        await h.Send("Bearer " + plaintext);
        await h.Send("Bearer " + plaintext);
        h.ReachedNext = false;
        var ctx = await h.Send("Bearer " + plaintext);

        Assert.False(h.ReachedNext);
        Assert.Equal(StatusCodes.Status429TooManyRequests, ctx.Response.StatusCode);
        Assert.False(string.IsNullOrEmpty(ctx.Response.Headers.RetryAfter.ToString()));
        Assert.Contains("rate limit", Harness.BodyOf(ctx));
    }

    [Fact]
    public async Task TheCallerRecoversOnceTheBudgetRefills()
    {
        var h = Harness.Create(rateLimit: 60);
        var (plaintext, _) = h.Mint();
        for (int i = 0; i < 60; i++) await h.Send("Bearer " + plaintext);
        h.ReachedNext = false;
        Assert.Equal(StatusCodes.Status429TooManyRequests, (await h.Send("Bearer " + plaintext)).Response.StatusCode);

        h.Clock.Advance(TimeSpan.FromSeconds(1));
        await h.Send("Bearer " + plaintext);

        Assert.True(h.ReachedNext);
    }

    [Fact]
    public async Task OneNoisyTokenDoesNotSpendAnothersBudget()
    {
        var h = Harness.Create(rateLimit: 1);
        var noisy = h.Service.Create(new ApiTokenCreateRequest
        { Name = "noisy", Scopes = new List<string> { ApiTokenScopes.BansWrite } });
        var quiet = h.Service.Create(new ApiTokenCreateRequest
        { Name = "quiet", Scopes = new List<string> { ApiTokenScopes.BansWrite } });

        await h.Send("Bearer " + noisy.Plaintext);
        Assert.Equal(StatusCodes.Status429TooManyRequests, (await h.Send("Bearer " + noisy.Plaintext)).Response.StatusCode);

        h.ReachedNext = false;
        await h.Send("Bearer " + quiet.Plaintext);
        Assert.True(h.ReachedNext);
    }

    [Fact]
    public async Task ARefusedCallDoesNotSpendBudget()
    {
        var h = Harness.Create(rateLimit: 1);
        var (plaintext, _) = h.Mint(ApiTokenScopes.BansRead);

        // Refused on scope, which happens before the bucket is touched.
        await h.Send("Bearer " + plaintext, "POST", "/api/bans");

        await h.Send("Bearer " + plaintext, "GET", "/api/bans");
        Assert.True(h.ReachedNext);
    }

    // ---- identity and telemetry ----

    [Fact]
    public async Task AnAcceptedRequestCarriesTheTokenAndItsAttribution()
    {
        var h = Harness.Create();
        var (plaintext, token) = h.Mint();

        var ctx = await h.Send("Bearer " + plaintext);

        Assert.Same(token, ApiTokenIdentity.Of(ctx));
        Assert.Equal("token:" + BotName, ApiTokenIdentity.Attribution(ctx));
    }

    [Fact]
    public void AnHmacRequestHasNoTokenAttribution()
        => Assert.Null(ApiTokenIdentity.Attribution(new DefaultHttpContext()));

    [Fact]
    public async Task AnAcceptedRequestStampsTheLastUse()
    {
        var h = Harness.Create();
        var (plaintext, token) = h.Mint();
        Assert.Equal(0, token.LastUsedAtUnix);

        h.Clock.Advance(TimeSpan.FromMinutes(3));
        await h.Send("Bearer " + plaintext);

        Assert.Equal(h.Clock.NowUnix, token.LastUsedAtUnix);
    }

    [Fact]
    public async Task ARefusedRequestDoesNotStampALastUse()
    {
        var h = Harness.Create();
        var (plaintext, token) = h.Mint(ApiTokenScopes.BansRead);

        await h.Send("Bearer " + plaintext, "POST", "/api/bans");

        Assert.Equal(0, token.LastUsedAtUnix);
    }

    // ---- what the logs may say ----

    [Fact]
    public async Task ARejectionLogsTheNameAndTheIdAndNeverTheSecret()
    {
        var h = Harness.Create();
        var (plaintext, token) = h.Mint(ApiTokenScopes.BansRead);

        await h.Send("Bearer " + plaintext, "POST", "/api/bans");

        Assert.Contains(BotName, h.Log.Text, StringComparison.Ordinal);
        Assert.Contains(token.Id, h.Log.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(plaintext, h.Log.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(plaintext[ApiTokenSecret.Prefix.Length..], h.Log.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(token.Hash, h.Log.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARejectionWithNoTokenBehindItInventsNothing()
    {
        var h = Harness.Create();
        string unknown = ApiTokenSecret.NewSecret();

        await h.Send("Bearer " + unknown);

        // No name, no id, and above all no prefix of the presented string: a log line made to
        // look complete is how part of a credential ends up in a log file.
        Assert.Contains("<none>", h.Log.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(unknown[..12], h.Log.Text, StringComparison.Ordinal);
    }


    [Fact]
    public async Task AnAcceptedRequestSaysNothing()
    {
        var h = Harness.Create();
        var (plaintext, _) = h.Mint();

        await h.Send("Bearer " + plaintext);

        Assert.Empty(h.Log.Lines);
    }
}
