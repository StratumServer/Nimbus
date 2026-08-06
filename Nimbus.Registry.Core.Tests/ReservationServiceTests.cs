using Nimbus.Registry.Services;
using Nimbus.Shared.Models;
using Xunit;

namespace Nimbus.Registry.Core.Tests;

/// <summary>
/// The mint rules on their own, without an HTTP handler or a proxy in front of them. Both
/// callers reach these through their own suite already (RegistryEndpointsTests over real HTTP,
/// InProcRegistryClientTests through the embedded client), so what is worth pinning here is the
/// part neither of them can express: the refusal reason as a value, and the TTL ceiling when
/// there is no ceiling.
/// </summary>
public class ReservationServiceTests
{
    private const string Uid = "uid-1";

    private sealed class Fixture
    {
        public required ReservationService Service { get; init; }
        public required ReservationStore Reservations { get; init; }
        public required BanStore Bans { get; init; }
        public required FakeClock Clock { get; init; }

        public static Fixture Create()
        {
            var clock = new FakeClock();
            var backends = new BackendRegistry(new RegistryConfig(), clock);
            var reservations = new ReservationStore(clock);
            var bans = new BanStore(clock);
            backends.Upsert(new BackendHeartbeat
            {
                ServerId = "creative",
                DisplayName = "creative",
                PublicHost = "10.0.0.1",
                PublicPort = 42420,
                MaxPlayers = 32,
            });
            return new Fixture
            {
                Service = new ReservationService(backends, reservations, bans, clock),
                Reservations = reservations,
                Bans = bans,
                Clock = clock,
            };
        }
    }

    private static ReservationMintRequest Request(int ttl = 60, int maxTtl = 300, string target = "creative")
        => new()
        {
            PlayerUid = Uid,
            PlayerName = "alice",
            SourceServerId = "hub",
            TargetServerId = target,
            TtlSeconds = ttl,
            MaxTtlSeconds = maxTtl,
        };

    [Fact]
    public void AMint_StampsTheReservationAndStoresIt()
    {
        var f = Fixture.Create();

        var result = f.Service.Mint(Request());

        Assert.True(result.Ok);
        Assert.Equal(ReservationMintStatus.Ok, result.Status);
        Assert.Equal("hub", result.Reservation!.SourceServerId);
        Assert.Equal(f.Clock.NowUnix + 60, result.Reservation.ExpiresAtUnix);
        Assert.NotNull(f.Reservations.Peek(result.Reservation.Id));
    }

    [Theory]
    [InlineData("", "creative")]
    [InlineData(Uid, "")]
    public void AMintMissingHalfOfItsSubject_IsRefusedWithoutTouchingTheStores(string uid, string target)
    {
        var f = Fixture.Create();

        var result = f.Service.Mint(new ReservationMintRequest { PlayerUid = uid, TargetServerId = target });

        Assert.Equal(ReservationMintStatus.MissingSubject, result.Status);
        Assert.Null(result.Reservation);
    }

    [Fact]
    public void AMintForABackendNobodyHasHeardFrom_SaysSoRatherThanStoringAnUnconsumableTicket()
    {
        var f = Fixture.Create();

        var result = f.Service.Mint(Request(target: "nowhere"));

        Assert.Equal(ReservationMintStatus.UnknownTarget, result.Status);
        Assert.Null(f.Reservations.ConsumeByUid(Uid, "nowhere"));
    }

    [Fact]
    public void AMintForABannedPlayer_SaysBannedSoBothCallersCanAnswerInTheirOwnLanguage()
    {
        var f = Fixture.Create();
        f.Bans.Add(new NetworkBan { PlayerUid = Uid, ServerId = "creative" });

        // The status is the whole point of the type: the endpoint turns this one into a 403 and
        // the in-proc client into a warning, and neither of them re-decides who is banned.
        Assert.Equal(ReservationMintStatus.Banned, f.Service.Mint(Request()).Status);
    }

    [Fact]
    public void ATtlAboveTheCeiling_IsClampedToIt()
    {
        var f = Fixture.Create();

        var result = f.Service.Mint(Request(ttl: 100000, maxTtl: 300));

        Assert.Equal(f.Clock.NowUnix + 300, result.Reservation!.ExpiresAtUnix);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-30)]
    public void ATtlThatIsNotAPositiveNumberOfSeconds_FallsBackToTheProtocolDefault(int ttl)
    {
        var f = Fixture.Create();

        var result = f.Service.Mint(Request(ttl: ttl));

        // An unset TTL must not mean "expires immediately", which would break every transfer on
        // the network rather than just looking wrong in the config.
        Assert.Equal(f.Clock.NowUnix + Nimbus.Shared.NimbusProtocol.DefaultReservationTtlSeconds,
            result.Reservation!.ExpiresAtUnix);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ACeilingThatIsNotAPositiveNumberOfSeconds_MeansNoCeiling(int maxTtl)
    {
        var f = Fixture.Create();

        var result = f.Service.Mint(Request(ttl: 100000, maxTtl: maxTtl));

        // Clamping to a zero ceiling would hand out reservations that expired the moment they
        // were minted, which is worse than the long TTL the operator asked for.
        Assert.Equal(f.Clock.NowUnix + 100000, result.Reservation!.ExpiresAtUnix);
    }

    [Fact]
    public void TwoMints_GetDistinctIds()
    {
        var f = Fixture.Create();

        var first = f.Service.Mint(Request()).Reservation!;
        var second = f.Service.Mint(Request()).Reservation!;

        Assert.NotEqual(first.Id, second.Id);
        Assert.NotNull(f.Reservations.Peek(first.Id));
        Assert.NotNull(f.Reservations.Peek(second.Id));
    }
}
