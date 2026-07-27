using Xunit;

namespace Nimbus.Proxy.Tests;

public class RedirectTargetingTests
{
    private static BackendEndpoint Backend(string host = "10.0.0.5", int port = 42421)
        => new() { Host = host, Port = port, ServerId = "hub" };

    [Fact]
    public void EmptyConfig_StampsBackendHostAndPort()
    {
        var choice = RedirectTargeting.Resolve(new TransfersConfig(), Backend());

        Assert.False(choice.ProxyStamped);
        Assert.Equal("10.0.0.5:42421", choice.HostString);
    }

    [Fact]
    public void EmptyConfig_ElidesVanillaDefaultPort()
    {
        var choice = RedirectTargeting.Resolve(new TransfersConfig(), Backend(port: 42420));

        Assert.False(choice.ProxyStamped);
        Assert.Equal("10.0.0.5", choice.HostString);
    }

    [Fact]
    public void ConfiguredAddress_StampsTheProxyVerbatim()
    {
        var transfers = new TransfersConfig { RedirectAddress = "play.example.org:42420" };

        var choice = RedirectTargeting.Resolve(transfers, Backend());

        Assert.True(choice.ProxyStamped);
        Assert.Equal("play.example.org:42420", choice.HostString);
    }

    [Fact]
    public void ConfiguredAddress_IsTrimmed()
    {
        var transfers = new TransfersConfig { RedirectAddress = "  play.example.org  " };

        var choice = RedirectTargeting.Resolve(transfers, Backend());

        Assert.True(choice.ProxyStamped);
        Assert.Equal("play.example.org", choice.HostString);
    }

    [Fact]
    public void WhitespaceOnlyAddress_FallsBackToBackendStamping()
    {
        var transfers = new TransfersConfig { RedirectAddress = "   " };

        var choice = RedirectTargeting.Resolve(transfers, Backend());

        Assert.False(choice.ProxyStamped);
        Assert.Equal("10.0.0.5:42421", choice.HostString);
    }

    private static ProxyConfigValidation Validate(string redirectAddress)
    {
        // Only the transfers section is under test; the default embedded-registry bind is
        // non-loopback with the default secret, which the validator rejects on purpose.
        var cfg = new ProxyConfig();
        cfg.Registry.Mode = "disabled";
        cfg.Transfers.RedirectAddress = redirectAddress;
        return ProxyConfigValidator.Validate(cfg);
    }

    [Theory]
    [InlineData("")]
    [InlineData("play.example.org")]
    [InlineData("play.example.org:42420")]
    [InlineData("203.0.113.7:42420")]
    public void Validator_AcceptsUsableAddresses(string address)
    {
        var result = Validate(address);

        Assert.DoesNotContain(result.Errors, e => e.Contains("transfers.redirect_address"));
    }

    [Theory]
    [InlineData("vs://play.example.org")]
    [InlineData("play.example.org:0")]
    [InlineData("play.example.org:70000")]
    [InlineData("play.example.org:")]
    [InlineData(":42420")]
    [InlineData("play example.org")]
    public void Validator_RejectsUndialableAddresses(string address)
    {
        var result = Validate(address);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("transfers.redirect_address"));
    }
}
