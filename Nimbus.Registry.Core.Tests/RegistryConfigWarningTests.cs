using Xunit;

namespace Nimbus.Registry.Core.Tests;

/// <summary>
/// The configurations that are accepted, start cleanly and then quietly do nothing. The standalone
/// registry has no config validator to run these through, so it prints them at boot, which is the
/// only moment an operator is looking.
///
/// Warnings rather than errors on purpose: nothing here breaks, which is exactly the problem. A
/// plain-HTTP bind that outside callers can reach makes bearer auth refuse every request it is
/// given, silently, and there is no other signal that would ever say so.
/// </summary>
public class RegistryConfigWarningTests
{
    [Fact]
    public void NothingIsSaidWhileTheSwitchIsOff()
    {
        var cfg = new RegistryConfig { BindUrl = "http://0.0.0.0:8765" };

        Assert.Empty(RegistryConfigWarnings.ApiTokens(cfg));
    }

    [Fact]
    public void APlainHttpBindThatOutsidersCanReachIsWarnedAbout()
    {
        var cfg = new RegistryConfig { BindUrl = "http://0.0.0.0:8765" };
        cfg.ApiTokens.Enabled = true;

        // 0.0.0.0 is not loopback: it is every interface, loopback included.
        Assert.Contains(RegistryConfigWarnings.ApiTokens(cfg),
            w => w.Contains("bearer auth will refuse every request"));
    }

    [Theory]
    [InlineData("http://127.0.0.1:8765")]
    [InlineData("http://localhost:8765")]
    [InlineData("https://registry.example.org")]
    [InlineData("not a url")]
    public void ABindTokensCanActuallyWorkOverIsNotWarnedAbout(string bind)
    {
        var cfg = new RegistryConfig { BindUrl = bind };
        cfg.ApiTokens.Enabled = true;

        Assert.DoesNotContain(RegistryConfigWarnings.ApiTokens(cfg),
            w => w.Contains("bearer auth will refuse every request"));
    }

    [Fact]
    public void ANonsenseRateLimitIsWarnedAbout()
    {
        var cfg = new RegistryConfig { BindUrl = "http://127.0.0.1:8765" };
        cfg.ApiTokens.Enabled = true;
        cfg.ApiTokens.RateLimitPerMinute = 0;

        Assert.Contains(RegistryConfigWarnings.ApiTokens(cfg), w => w.Contains("60 per token"));
    }

    [Fact]
    public void TrustingForwardedProtoIsWarnedAboutForWhatItIs()
    {
        var cfg = new RegistryConfig { BindUrl = "http://0.0.0.0:8765" };
        cfg.ApiTokens.Enabled = true;
        cfg.ApiTokens.TrustForwardedProto = true;

        Assert.Contains(RegistryConfigWarnings.ApiTokens(cfg), w => w.Contains("X-Forwarded-Proto"));
    }
}
