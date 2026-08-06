using Nimbus.Registry.Services;
using Nimbus.Shared.Models;
using Xunit;

namespace Nimbus.Proxy.Tests;

/// <summary>
/// The [api_tokens] settings on the way from proxy config into the embedded registry, and the two
/// ways of configuring them into doing nothing.
///
/// Both of those are warnings rather than errors on purpose: neither breaks anything, which is
/// exactly the problem. A plain-HTTP bind that outside callers can reach makes bearer auth refuse
/// every request it is given, silently, with no other signal an operator would ever see.
/// </summary>
public class ApiTokenConfigTests
{
    private static ProxyConfig Embedded(Action<RegistryConfig> configure)
    {
        var cfg = new ProxyConfig();
        cfg.Registry.Mode = "embedded";
        // The default bind is 0.0.0.0 with the default secret, which the validator rejects for
        // reasons that have nothing to do with tokens.
        cfg.Registry.EmbeddedBind = "http://127.0.0.1:8765";
        configure(cfg.Registry);
        return cfg;
    }

    private static ProxyConfigValidation Validate(Action<RegistryConfig> configure)
        => ProxyConfigValidator.Validate(Embedded(configure));

    // ---- defaults ----

    [Fact]
    public void TheSwitchIsOffAndTheRateIsSixtyOutOfTheBox()
    {
        var cfg = new ProxyConfig().Registry;

        Assert.False(cfg.ApiTokensEnabled);
        Assert.Equal(60, cfg.ApiTokensRateLimitPerMinute);
        Assert.False(cfg.ApiTokensTrustForwardedProto);
    }

    [Fact]
    public void NothingIsSaidWhileTheSwitchIsOff()
    {
        var result = Validate(r =>
        {
            r.ApiTokensEnabled = false;
            // Nonsense that would be complained about if anything were reading it.
            r.ApiTokensRateLimitPerMinute = 0;
            r.EmbeddedBind = "http://0.0.0.0:8765";
            r.EmbeddedSharedSecret = "a-real-secret";
        });

        Assert.DoesNotContain(result.Warnings, w => w.Contains("api_tokens"));
        Assert.DoesNotContain(result.Errors, e => e.Contains("api_tokens"));
    }

    // ---- the rate limit ----

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ANonsenseRateLimitIsAnError(int rate)
    {
        var result = Validate(r => { r.ApiTokensEnabled = true; r.ApiTokensRateLimitPerMinute = rate; });

        Assert.Contains(result.Errors, e => e.Contains("api_tokens_rate_limit_per_minute"));
    }

    [Fact]
    public void AUsableRateLimitIsNotComplainedAbout()
    {
        var result = Validate(r => { r.ApiTokensEnabled = true; r.ApiTokensRateLimitPerMinute = 120; });

        Assert.DoesNotContain(result.Errors, e => e.Contains("api_tokens_rate_limit_per_minute"));
    }

    // ---- the transport the tokens will meet ----

    [Fact]
    public void APlainHttpBindThatOutsidersCanReachIsWarnedAbout()
    {
        var result = Validate(r =>
        {
            r.ApiTokensEnabled = true;
            r.EmbeddedBind = "http://0.0.0.0:8765";
            r.EmbeddedSharedSecret = "a-real-secret";
        });

        Assert.Contains(result.Warnings, w => w.Contains("bearer auth will refuse every request"));
    }

    [Theory]
    [InlineData("http://127.0.0.1:8765")]
    [InlineData("http://localhost:8765")]
    [InlineData("https://registry.example.org")]
    [InlineData("")]
    public void ABindTokensCanActuallyWorkOverIsNotWarnedAbout(string bind)
    {
        var result = Validate(r =>
        {
            r.ApiTokensEnabled = true;
            r.EmbeddedBind = bind;
            r.EmbeddedSharedSecret = "a-real-secret";
        });

        Assert.DoesNotContain(result.Warnings, w => w.Contains("bearer auth will refuse every request"));
    }

    [Fact]
    public void TrustingForwardedProtoIsWarnedAboutForWhatItIs()
    {
        var result = Validate(r =>
        {
            r.ApiTokensEnabled = true;
            r.ApiTokensTrustForwardedProto = true;
            r.EmbeddedBind = "http://0.0.0.0:8765";
            r.EmbeddedSharedSecret = "a-real-secret";
        });

        // The refusal warning would be wrong here, because the tokens will work. What is worth
        // saying is what has been traded for that.
        Assert.DoesNotContain(result.Warnings, w => w.Contains("bearer auth will refuse every request"));
        Assert.Contains(result.Warnings, w => w.Contains("X-Forwarded-Proto"));
    }

    [Fact]
    public void TheSettingsAreInertInRemoteModeAndSaySo()
    {
        var cfg = new ProxyConfig();
        cfg.Registry.Mode = "remote";
        cfg.Registry.Url = "https://registry.example.org";
        cfg.Registry.SharedSecret = "a-real-secret";
        cfg.Registry.ApiTokensEnabled = true;

        var result = ProxyConfigValidator.Validate(cfg);

        Assert.Contains(result.Warnings, w => w.Contains("only applies to the embedded registry"));
    }

    // ---- embedded wiring ----

    [Fact]
    public async Task TheEmbeddedRegistryGetsTheSettingsAndItsOwnTokenFile()
    {
        string dir = Path.Combine(Path.GetTempPath(), "nimbus-token-embedded-" + Guid.NewGuid().ToString("N"));
        try
        {
            var cfg = new ProxyConfig();
            cfg.Registry.Mode = "embedded";
            cfg.Registry.EmbeddedBind = "";
            cfg.Registry.EmbeddedStateDir = dir;
            cfg.Registry.ApiTokensEnabled = true;

            await using var host = ProxyRegistryHost.Build(cfg, CancellationToken.None);
            Assert.NotNull(host.Client);
            var created = await host.Client.CreateApiTokenAsync(new ApiTokenCreateRequest
            {
                Name = "discord-bot",
                Scopes = new List<string> { ApiTokenScopes.WhitelistWrite },
            }, CancellationToken.None);

            Assert.NotNull(created);
            Assert.StartsWith(ApiTokenSecret.Prefix, created.Token);
            // Minting works in the no-listener mode as well: an operator preparing a bot still
            // needs the credential, and the registry that will answer it reads the same file.
            string file = Path.Combine(dir, RegistryStateFiles.TokensFileName);
            Assert.True(File.Exists(file));
            Assert.DoesNotContain(created.Token, await File.ReadAllTextAsync(file), StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* never created */ }
        }
    }
}
