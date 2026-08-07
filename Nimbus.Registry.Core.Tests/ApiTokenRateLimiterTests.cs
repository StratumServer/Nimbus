using Nimbus.Registry.Services;
using Xunit;

namespace Nimbus.Registry.Core.Tests;

/// <summary>
/// The per-token budget. Per token and not only per IP, because a compromised bot credential can
/// do a lot of damage quickly from an address that has done nothing wrong, and because the
/// addresses a hosted bot calls from are not something an operator can enumerate in advance.
///
/// Time moves by advancing a clock, never by sleeping: a rate limiter tested with sleeps is a
/// test that fails on a loaded CI machine and tells nobody anything when it does.
/// </summary>
public class ApiTokenRateLimiterTests
{
    private readonly FakeClock clock = new();

    [Fact]
    public void ATokenMayBurstUpToAMinutesWorth()
    {
        var limiter = new ApiTokenRateLimiter(10, clock);

        for (int i = 0; i < 10; i++)
            Assert.True(limiter.TryTake("token-1", out _), $"call {i + 1} was refused");

        Assert.False(limiter.TryTake("token-1", out _));
    }

    [Fact]
    public void ARefusalNamesAWaitThatIsNeverZero()
    {
        var limiter = new ApiTokenRateLimiter(60, clock);
        for (int i = 0; i < 60; i++) limiter.TryTake("token-1", out _);

        Assert.False(limiter.TryTake("token-1", out int retryAfter));

        // A Retry-After of 0 invites the immediate retry that caused the 429.
        Assert.True(retryAfter >= 1, $"retry-after was {retryAfter}");
    }

    [Fact]
    public void TheBudgetRefillsAsTimePasses()
    {
        var limiter = new ApiTokenRateLimiter(60, clock);
        for (int i = 0; i < 60; i++) limiter.TryTake("token-1", out _);
        Assert.False(limiter.TryTake("token-1", out _));

        clock.Advance(TimeSpan.FromSeconds(1));

        Assert.True(limiter.TryTake("token-1", out _));
        Assert.False(limiter.TryTake("token-1", out _));
    }

    [Fact]
    public void TheRefillStopsAtTheCeiling()
    {
        var limiter = new ApiTokenRateLimiter(5, clock);
        for (int i = 0; i < 5; i++) limiter.TryTake("token-1", out _);

        // An hour of silence does not buy an hour of burst.
        clock.Advance(TimeSpan.FromHours(1));

        for (int i = 0; i < 5; i++) Assert.True(limiter.TryTake("token-1", out _));
        Assert.False(limiter.TryTake("token-1", out _));
    }

    [Fact]
    public void OneTokenRunningOutDoesNotTouchAnother()
    {
        var limiter = new ApiTokenRateLimiter(3, clock);
        for (int i = 0; i < 3; i++) limiter.TryTake("noisy", out _);

        Assert.False(limiter.TryTake("noisy", out _));
        Assert.True(limiter.TryTake("quiet", out _));
    }

    [Fact]
    public void ATokenSeenForTheFirstTimeStartsFull()
    {
        var limiter = new ApiTokenRateLimiter(2, clock);
        clock.Advance(TimeSpan.FromDays(7));

        Assert.True(limiter.TryTake("fresh", out _));
        Assert.True(limiter.TryTake("fresh", out _));
        Assert.False(limiter.TryTake("fresh", out _));
    }

    [Fact]
    public void AClockThatWentBackwardsHandsOutNoCredit()
    {
        var limiter = new ApiTokenRateLimiter(2, clock);
        limiter.TryTake("token-1", out _);
        limiter.TryTake("token-1", out _);

        clock.Advance(TimeSpan.FromMinutes(-5));

        Assert.False(limiter.TryTake("token-1", out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-30)]
    public void ANonsenseLimitFallsBackToTheDefaultRatherThanToNoneOrToNothing(int configured)
    {
        var limiter = new ApiTokenRateLimiter(configured, clock);

        // Not zero, which would be an outage for every integration, and not unlimited, which
        // would silently drop the protection. ProxyConfigValidator says so out loud as well.
        for (int i = 0; i < ApiTokenRateLimiter.DefaultPerMinute; i++)
            Assert.True(limiter.TryTake("token-1", out _), $"call {i + 1} was refused");
        Assert.False(limiter.TryTake("token-1", out _));
    }

    [Fact]
    public void TheLimitComesFromTheRegistryConfig()
    {
        var cfg = new RegistryConfig();
        cfg.ApiTokens.RateLimitPerMinute = 2;
        var limiter = new ApiTokenRateLimiter(cfg, clock);

        Assert.True(limiter.TryTake("token-1", out _));
        Assert.True(limiter.TryTake("token-1", out _));
        Assert.False(limiter.TryTake("token-1", out _));
    }

    [Fact]
    public void SixtyPerMinuteIsTheDefault()
        => Assert.Equal(ApiTokenRateLimiter.DefaultPerMinute, new RegistryConfig().ApiTokens.RateLimitPerMinute);
}
