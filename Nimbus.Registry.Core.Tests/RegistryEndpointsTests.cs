using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Nimbus.Shared;
using Nimbus.Shared.Models;
using Nimbus.Shared.Security;
using Xunit;

namespace Nimbus.Registry.Core.Tests;

/// <summary>
/// Boots the real registry pipeline (RegistryHosting + HmacAuthMiddleware + Endpoints) on
/// a loopback port and drives it over actual HTTP, so the auth middleware, the routing and
/// the JSON contracts are exercised exactly as a backend or proxy sees them.
/// </summary>
public class RegistryEndpointsTests
{
    private const string Secret = "endpoint-test-secret";

    private sealed class Host : IAsyncDisposable
    {
        public required WebApplication App { get; init; }
        public required string BaseUrl { get; init; }
        public HttpClient Client { get; } = new();

        public static async Task<Host> StartAsync(RegistryConfig? cfg = null)
        {
            cfg ??= new RegistryConfig { SharedSecret = Secret };
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.AddNimbusRegistry(cfg, withMasterServer: false);
            var app = builder.Build();
            app.UseNimbusRegistry();
            await app.StartAsync();
            return new Host { App = app, BaseUrl = app.Urls.First() };
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await App.StopAsync();
            await App.DisposeAsync();
        }
    }

    /// <summary>Builds a request carrying the four X-Nimbus-* headers. Every knob can be
    /// bent out of shape to probe a specific middleware rejection.</summary>
    private static HttpRequestMessage Signed(
        HttpMethod method, string baseUrl, string pathAndQuery,
        string secret = Secret, object? body = null,
        long? timestamp = null, string? nonce = null, int? protocol = null, string? signature = null)
    {
        byte[] bytes = body is null ? Array.Empty<byte>() : JsonSerializer.SerializeToUtf8Bytes(body);
        var msg = new HttpRequestMessage(method, baseUrl.TrimEnd('/') + pathAndQuery);
        if (body is not null)
        {
            msg.Content = new ByteArrayContent(bytes);
            msg.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        }

        long ts = timestamp ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string n = nonce ?? HmacSigner.NewNonce();
        int proto = protocol ?? NimbusProtocol.ProtocolVersion;
        string path = pathAndQuery.Split('?')[0];
        string sig = signature
            ?? HmacSigner.Sign(secret, HmacSigner.CanonicalString(method.Method, path, proto, ts, n, bytes));

        msg.Headers.Add(NimbusProtocol.SignatureHeader, sig);
        msg.Headers.Add(NimbusProtocol.TimestampHeader, ts.ToString());
        msg.Headers.Add(NimbusProtocol.NonceHeader, n);
        msg.Headers.Add(NimbusProtocol.ProtocolHeader, proto.ToString());
        return msg;
    }

    private static BackendHeartbeat Heartbeat(string id = "backend-1") => new()
    {
        ServerId = id,
        DisplayName = id,
        PublicHost = "10.0.0.1",
        PublicPort = 42421,
        Players = 3,
        MaxPlayers = 32,
    };

    // ---- middleware ----

    [Fact]
    public async Task HealthAndRoot_AreUnauthenticated()
    {
        await using var host = await Host.StartAsync();

        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync(host.BaseUrl + "/health")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync(host.BaseUrl + "/")).StatusCode);
    }

    [Fact]
    public async Task Api_WithoutNimbusHeaders_Is401()
    {
        await using var host = await Host.StartAsync();

        var resp = await host.Client.GetAsync(host.BaseUrl + "/api/servers");

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Contains("missing nimbus headers", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Api_WithTheWrongSecret_Is401()
    {
        await using var host = await Host.StartAsync();

        var resp = await host.Client.SendAsync(
            Signed(HttpMethod.Get, host.BaseUrl, "/api/servers", secret: "not-the-secret"));

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Contains("bad signature", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Api_OutsideTheClockSkewWindow_Is401()
    {
        await using var host = await Host.StartAsync();
        long stale = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - NimbusProtocol.MaxClockSkewSeconds - 5;

        var resp = await host.Client.SendAsync(
            Signed(HttpMethod.Get, host.BaseUrl, "/api/servers", timestamp: stale));

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Contains("clock skew", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Api_ReplayingANonce_Is401()
    {
        await using var host = await Host.StartAsync();
        string nonce = HmacSigner.NewNonce();

        var first = await host.Client.SendAsync(Signed(HttpMethod.Get, host.BaseUrl, "/api/servers", nonce: nonce));
        var replay = await host.Client.SendAsync(Signed(HttpMethod.Get, host.BaseUrl, "/api/servers", nonce: nonce));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        Assert.Contains("replay", await replay.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Api_WithAForeignProtocolVersion_Is401()
    {
        await using var host = await Host.StartAsync();

        var resp = await host.Client.SendAsync(
            Signed(HttpMethod.Get, host.BaseUrl, "/api/servers", protocol: NimbusProtocol.ProtocolVersion + 1));

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Contains("protocol mismatch", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Api_SignedWithARotationSecret_IsAccepted()
    {
        // Rotation story: promote the new secret, keep the old one in AcceptedSecrets
        // until every backend has redeployed.
        var cfg = new RegistryConfig { SharedSecret = "brand-new", AcceptedSecrets = new[] { Secret } };
        await using var host = await Host.StartAsync(cfg);

        var withOld = await host.Client.SendAsync(Signed(HttpMethod.Get, host.BaseUrl, "/api/servers", secret: Secret));
        var withNew = await host.Client.SendAsync(Signed(HttpMethod.Get, host.BaseUrl, "/api/servers", secret: "brand-new"));

        Assert.Equal(HttpStatusCode.OK, withOld.StatusCode);
        Assert.Equal(HttpStatusCode.OK, withNew.StatusCode);
    }

    // ---- endpoints ----

    [Fact]
    public async Task Heartbeat_UpsertsTheBackend_VisibleInTheSnapshot()
    {
        await using var host = await Host.StartAsync();

        var beat = await host.Client.SendAsync(
            Signed(HttpMethod.Post, host.BaseUrl, "/api/heartbeat", body: Heartbeat()));
        Assert.Equal(HttpStatusCode.OK, beat.StatusCode);
        var beatBody = await beat.Content.ReadFromJsonAsync<BackendHeartbeatResponse>();
        Assert.True(beatBody!.Ok);

        var servers = await host.Client.SendAsync(Signed(HttpMethod.Get, host.BaseUrl, "/api/servers"));
        var snapshot = await servers.Content.ReadFromJsonAsync<NetworkSnapshot>();

        var backend = Assert.Single(snapshot!.Backends);
        Assert.Equal("backend-1", backend.ServerId);
        Assert.False(backend.Stale);
        Assert.Equal(3, snapshot.TotalPlayers);
        Assert.Equal(32, snapshot.TotalCapacity);
    }

    [Fact]
    public async Task Heartbeat_WithoutAServerId_Is400()
    {
        await using var host = await Host.StartAsync();

        var resp = await host.Client.SendAsync(
            Signed(HttpMethod.Post, host.BaseUrl, "/api/heartbeat", body: new { DisplayName = "anon" }));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Reservation_MintedThenConsumedById_IsSingleUse()
    {
        await using var host = await Host.StartAsync();
        await host.Client.SendAsync(Signed(HttpMethod.Post, host.BaseUrl, "/api/heartbeat", body: Heartbeat()));

        var mint = await host.Client.SendAsync(Signed(HttpMethod.Post, host.BaseUrl, "/api/reservations",
            body: new { PlayerUid = "uid-1", PlayerName = "alice", TargetServerId = "backend-1", RealRemoteIp = "203.0.113.9" }));
        Assert.Equal(HttpStatusCode.OK, mint.StatusCode);
        var minted = await mint.Content.ReadFromJsonAsync<ReservationResponse>();
        string id = minted!.Reservation!.Id;
        Assert.Equal("203.0.113.9", minted.Reservation.RealRemoteIp);

        var consume = await host.Client.SendAsync(
            Signed(HttpMethod.Post, host.BaseUrl, $"/api/reservations/{id}/consume?target=backend-1"));
        Assert.Equal(HttpStatusCode.OK, consume.StatusCode);

        var again = await host.Client.SendAsync(
            Signed(HttpMethod.Post, host.BaseUrl, $"/api/reservations/{id}/consume?target=backend-1"));
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
    }

    [Fact]
    public async Task Ban_MintedThenListedThenLifted()
    {
        await using var host = await Host.StartAsync();

        var added = await host.Client.SendAsync(Signed(HttpMethod.Post, host.BaseUrl, "/api/bans",
            body: new { PlayerUid = "uid-b1", PlayerName = "griefer", Reason = "grief", BannedBy = "admin" }));
        Assert.Equal(HttpStatusCode.OK, added.StatusCode);
        var ban = await added.Content.ReadFromJsonAsync<BanResponse>();
        Assert.True(ban!.Ok);
        Assert.True(ban.Ban!.IsNetworkWide);
        Assert.Equal(0, ban.Ban.ExpiresAtUnix);

        var listed = await host.Client.SendAsync(Signed(HttpMethod.Get, host.BaseUrl, "/api/bans"));
        var list = await listed.Content.ReadFromJsonAsync<BanListResponse>();
        Assert.Single(list!.Bans);
        Assert.Equal("griefer", list.Bans[0].PlayerName);

        var lifted = await host.Client.SendAsync(
            Signed(HttpMethod.Post, host.BaseUrl, "/api/bans/lift?uid=uid-b1"));
        Assert.Equal(HttpStatusCode.OK, lifted.StatusCode);

        var again = await host.Client.SendAsync(
            Signed(HttpMethod.Post, host.BaseUrl, "/api/bans/lift?uid=uid-b1"));
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);

        var empty = await host.Client.SendAsync(Signed(HttpMethod.Get, host.BaseUrl, "/api/bans"));
        Assert.Empty((await empty.Content.ReadFromJsonAsync<BanListResponse>())!.Bans);
    }

    [Fact]
    public async Task Ban_WithADuration_GetsAnExpiry()
    {
        await using var host = await Host.StartAsync();

        var added = await host.Client.SendAsync(Signed(HttpMethod.Post, host.BaseUrl, "/api/bans",
            body: new { PlayerUid = "uid-b2", DurationSeconds = 3600 }));

        var ban = await added.Content.ReadFromJsonAsync<BanResponse>();
        Assert.True(ban!.Ban!.ExpiresAtUnix > 0);
    }

    [Fact]
    public async Task ScopedBan_IsLiftedOnlyWithItsServerId()
    {
        await using var host = await Host.StartAsync();
        await host.Client.SendAsync(Signed(HttpMethod.Post, host.BaseUrl, "/api/bans",
            body: new { PlayerUid = "uid-b3", ServerId = "creative" }));

        // Lifting without a scope targets the network-wide entry, which does not exist here.
        var wrongScope = await host.Client.SendAsync(
            Signed(HttpMethod.Post, host.BaseUrl, "/api/bans/lift?uid=uid-b3"));
        Assert.Equal(HttpStatusCode.NotFound, wrongScope.StatusCode);

        var rightScope = await host.Client.SendAsync(
            Signed(HttpMethod.Post, host.BaseUrl, "/api/bans/lift?uid=uid-b3&server=creative"));
        Assert.Equal(HttpStatusCode.OK, rightScope.StatusCode);
    }

    [Fact]
    public async Task Ban_WithoutAPlayerUid_Is400()
    {
        await using var host = await Host.StartAsync();

        var resp = await host.Client.SendAsync(Signed(HttpMethod.Post, host.BaseUrl, "/api/bans",
            body: new { Reason = "no uid" }));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Bans_RequireASignature()
    {
        await using var host = await Host.StartAsync();

        var resp = await host.Client.GetAsync($"{host.BaseUrl}/api/bans");

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task WhitelistEntry_AddedThenListedThenRemoved()
    {
        await using var host = await Host.StartAsync();

        var added = await host.Client.SendAsync(Signed(HttpMethod.Post, host.BaseUrl, "/api/whitelist",
            body: new { PlayerUid = "uid-w1", PlayerName = "builder", Note = "beta tester", AddedBy = "admin" }));
        Assert.Equal(HttpStatusCode.OK, added.StatusCode);
        var entry = await added.Content.ReadFromJsonAsync<WhitelistResponse>();
        Assert.True(entry!.Ok);
        Assert.True(entry.Entry!.IsNetworkWide);
        Assert.Equal(0, entry.Entry.ExpiresAtUnix);

        var listed = await host.Client.SendAsync(Signed(HttpMethod.Get, host.BaseUrl, "/api/whitelist"));
        var list = await listed.Content.ReadFromJsonAsync<WhitelistListResponse>();
        Assert.Single(list!.Entries);
        Assert.Equal("builder", list.Entries[0].PlayerName);

        var removed = await host.Client.SendAsync(
            Signed(HttpMethod.Post, host.BaseUrl, "/api/whitelist/remove?uid=uid-w1"));
        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);

        var again = await host.Client.SendAsync(
            Signed(HttpMethod.Post, host.BaseUrl, "/api/whitelist/remove?uid=uid-w1"));
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);

        var empty = await host.Client.SendAsync(Signed(HttpMethod.Get, host.BaseUrl, "/api/whitelist"));
        Assert.Empty((await empty.Content.ReadFromJsonAsync<WhitelistListResponse>())!.Entries);
    }

    [Fact]
    public async Task WhitelistEntry_WithADuration_GetsAnExpiry()
    {
        await using var host = await Host.StartAsync();

        var added = await host.Client.SendAsync(Signed(HttpMethod.Post, host.BaseUrl, "/api/whitelist",
            body: new { PlayerUid = "uid-w2", DurationSeconds = 3600 }));

        var entry = await added.Content.ReadFromJsonAsync<WhitelistResponse>();
        Assert.True(entry!.Entry!.ExpiresAtUnix > 0);
    }

    [Fact]
    public async Task ScopedWhitelistEntry_CoversItsOwnBackendOnly()
    {
        await using var host = await Host.StartAsync();
        await host.Client.SendAsync(Signed(HttpMethod.Post, host.BaseUrl, "/api/whitelist",
            body: new { PlayerUid = "uid-w3", ServerId = "staff" }));

        var listed = await host.Client.SendAsync(Signed(HttpMethod.Get, host.BaseUrl, "/api/whitelist"));
        var scoped = Assert.Single((await listed.Content.ReadFromJsonAsync<WhitelistListResponse>())!.Entries);
        Assert.False(scoped.IsNetworkWide);
        Assert.True(scoped.Covers("staff"));
        Assert.False(scoped.Covers("creative"));
        // An empty serverId asks about the network itself, which a scoped entry never covers.
        Assert.False(scoped.Covers(""));

        // Removing without the scope targets the network-wide entry, which does not exist here.
        var wrongScope = await host.Client.SendAsync(
            Signed(HttpMethod.Post, host.BaseUrl, "/api/whitelist/remove?uid=uid-w3"));
        Assert.Equal(HttpStatusCode.NotFound, wrongScope.StatusCode);

        var rightScope = await host.Client.SendAsync(
            Signed(HttpMethod.Post, host.BaseUrl, "/api/whitelist/remove?uid=uid-w3&server=staff"));
        Assert.Equal(HttpStatusCode.OK, rightScope.StatusCode);
    }

    [Fact]
    public async Task NetworkWideWhitelistEntry_CoversEveryBackend()
    {
        await using var host = await Host.StartAsync();
        await host.Client.SendAsync(Signed(HttpMethod.Post, host.BaseUrl, "/api/whitelist",
            body: new { PlayerUid = "uid-w4" }));

        var listed = await host.Client.SendAsync(Signed(HttpMethod.Get, host.BaseUrl, "/api/whitelist"));
        var entry = Assert.Single((await listed.Content.ReadFromJsonAsync<WhitelistListResponse>())!.Entries);
        Assert.True(entry.Covers("creative"));
        Assert.True(entry.Covers("staff"));
        Assert.True(entry.Covers(null));
    }

    [Fact]
    public async Task WhitelistEntry_WithoutAPlayerUid_Is400()
    {
        await using var host = await Host.StartAsync();

        var resp = await host.Client.SendAsync(Signed(HttpMethod.Post, host.BaseUrl, "/api/whitelist",
            body: new { Note = "no uid" }));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Whitelist_RequiresASignature()
    {
        await using var host = await Host.StartAsync();

        var resp = await host.Client.GetAsync($"{host.BaseUrl}/api/whitelist");

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Whitelist_DoesNotRefuseAReservation()
    {
        await using var host = await Host.StartAsync();
        await host.Client.SendAsync(Signed(HttpMethod.Post, host.BaseUrl, "/api/heartbeat", body: Heartbeat()));
        await host.Client.SendAsync(Signed(HttpMethod.Post, host.BaseUrl, "/api/whitelist",
            body: new { PlayerUid = "uid-w5", ServerId = "backend-1" }));

        // Deliberate: enforcement lives in proxy config, so an unlisted player still gets a
        // reservation here and the proxy is the one that decides whether to seat them.
        var minted = await host.Client.SendAsync(Signed(HttpMethod.Post, host.BaseUrl, "/api/reservations",
            body: new { PlayerUid = "uid-not-listed", TargetServerId = "backend-1" }));

        Assert.Equal(HttpStatusCode.OK, minted.StatusCode);
    }

    [Fact]
    public async Task Reservation_CarriesTheClientTransferId_ThroughMintAndConsume()
    {
        await using var host = await Host.StartAsync();
        await host.Client.SendAsync(Signed(HttpMethod.Post, host.BaseUrl, "/api/heartbeat", body: Heartbeat()));

        var mint = await host.Client.SendAsync(Signed(HttpMethod.Post, host.BaseUrl, "/api/reservations",
            body: new { PlayerUid = "uid-9", PlayerName = "carol", TargetServerId = "backend-1", ClientTransferId = "A1B2C3D4E5" }));
        Assert.Equal(HttpStatusCode.OK, mint.StatusCode);
        var minted = await mint.Content.ReadFromJsonAsync<ReservationResponse>();
        Assert.Equal("A1B2C3D4E5", minted!.Reservation!.ClientTransferId);

        // The target backend consumes by uid on join; the transferId must survive the trip
        // so the target can send the seamless commit (#19).
        var consume = await host.Client.SendAsync(
            Signed(HttpMethod.Post, host.BaseUrl, "/api/reservations/consume-by-uid?uid=uid-9&target=backend-1"));
        Assert.Equal(HttpStatusCode.OK, consume.StatusCode);
        var consumed = await consume.Content.ReadFromJsonAsync<ReservationResponse>();
        Assert.Equal("A1B2C3D4E5", consumed!.Reservation!.ClientTransferId);
    }

    [Fact]
    public async Task Reservation_WithoutAClientTransferId_DefaultsToEmpty()
    {
        await using var host = await Host.StartAsync();
        await host.Client.SendAsync(Signed(HttpMethod.Post, host.BaseUrl, "/api/heartbeat", body: Heartbeat()));

        var mint = await host.Client.SendAsync(Signed(HttpMethod.Post, host.BaseUrl, "/api/reservations",
            body: new { PlayerUid = "uid-10", TargetServerId = "backend-1" }));

        var minted = await mint.Content.ReadFromJsonAsync<ReservationResponse>();
        Assert.Equal("", minted!.Reservation!.ClientTransferId);
    }

    [Fact]
    public async Task Reservation_ForAnUnregisteredTarget_Is404()
    {
        await using var host = await Host.StartAsync();

        var resp = await host.Client.SendAsync(Signed(HttpMethod.Post, host.BaseUrl, "/api/reservations",
            body: new { PlayerUid = "uid-1", TargetServerId = "nowhere" }));

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Reservation_ForABannedPair_IsRefused_ButOtherBackendsStillMint()
    {
        await using var host = await Host.StartAsync();
        await host.Client.SendAsync(Signed(HttpMethod.Post, host.BaseUrl, "/api/heartbeat", body: Heartbeat()));
        await host.Client.SendAsync(Signed(HttpMethod.Post, host.BaseUrl, "/api/heartbeat", body: Heartbeat("backend-2")));
        await host.Client.SendAsync(Signed(HttpMethod.Post, host.BaseUrl, "/api/bans",
            body: new { PlayerUid = "uid-1", ServerId = "backend-1", Reason = "grief" }));

        var refused = await host.Client.SendAsync(Signed(HttpMethod.Post, host.BaseUrl, "/api/reservations",
            body: new { PlayerUid = "uid-1", TargetServerId = "backend-1" }));
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        var body = await refused.Content.ReadFromJsonAsync<ReservationResponse>();
        Assert.False(body!.Ok);
        Assert.Null(body.Reservation);

        // The ban covers one backend, so the rest of the network still mints for this player.
        var allowed = await host.Client.SendAsync(Signed(HttpMethod.Post, host.BaseUrl, "/api/reservations",
            body: new { PlayerUid = "uid-1", TargetServerId = "backend-2" }));
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);

        // And a network-wide ban closes every backend to them.
        await host.Client.SendAsync(Signed(HttpMethod.Post, host.BaseUrl, "/api/bans",
            body: new { PlayerUid = "uid-1", Reason = "grief" }));
        var everywhere = await host.Client.SendAsync(Signed(HttpMethod.Post, host.BaseUrl, "/api/reservations",
            body: new { PlayerUid = "uid-1", TargetServerId = "backend-2" }));
        Assert.Equal(HttpStatusCode.Forbidden, everywhere.StatusCode);
    }

    [Fact]
    public async Task Reservation_TtlIsClampedToTheConfiguredMaximum()
    {
        var cfg = new RegistryConfig { SharedSecret = Secret, MaxReservationTtlSeconds = 120 };
        await using var host = await Host.StartAsync(cfg);
        await host.Client.SendAsync(Signed(HttpMethod.Post, host.BaseUrl, "/api/heartbeat", body: Heartbeat()));

        var mint = await host.Client.SendAsync(Signed(HttpMethod.Post, host.BaseUrl, "/api/reservations",
            body: new { PlayerUid = "uid-1", TargetServerId = "backend-1", TtlSeconds = 99999 }));
        var minted = await mint.Content.ReadFromJsonAsync<ReservationResponse>();

        long lifetime = minted!.Reservation!.ExpiresAtUnix - DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Assert.InRange(lifetime, 1, 121);
    }

    [Fact]
    public async Task ConsumeByUid_WorksOverHttp_AndIsSingleUse()
    {
        await using var host = await Host.StartAsync();
        await host.Client.SendAsync(Signed(HttpMethod.Post, host.BaseUrl, "/api/heartbeat", body: Heartbeat()));
        await host.Client.SendAsync(Signed(HttpMethod.Post, host.BaseUrl, "/api/reservations",
            body: new { PlayerUid = "uid-7", TargetServerId = "backend-1" }));

        var consume = await host.Client.SendAsync(
            Signed(HttpMethod.Post, host.BaseUrl, "/api/reservations/consume-by-uid?uid=uid-7&target=backend-1"));
        Assert.Equal(HttpStatusCode.OK, consume.StatusCode);
        var consumed = await consume.Content.ReadFromJsonAsync<ReservationResponse>();
        Assert.Equal("uid-7", consumed!.Reservation!.PlayerUid);

        var again = await host.Client.SendAsync(
            Signed(HttpMethod.Post, host.BaseUrl, "/api/reservations/consume-by-uid?uid=uid-7&target=backend-1"));
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
    }

    [Fact]
    public async Task TransferIntents_PostedThenDrained_AtMostOnce()
    {
        await using var host = await Host.StartAsync();
        await host.Client.SendAsync(Signed(HttpMethod.Post, host.BaseUrl, "/api/heartbeat", body: Heartbeat()));

        var post = await host.Client.SendAsync(Signed(HttpMethod.Post, host.BaseUrl, "/api/transfer-intents",
            body: new { PlayerUid = "uid-1", TargetServerId = "backend-1", Mode = "redirect" }));
        Assert.Equal(HttpStatusCode.OK, post.StatusCode);

        var unknown = await host.Client.SendAsync(Signed(HttpMethod.Post, host.BaseUrl, "/api/transfer-intents",
            body: new { PlayerUid = "uid-1", TargetServerId = "nowhere" }));
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

        var drain = await host.Client.SendAsync(Signed(HttpMethod.Post, host.BaseUrl, "/api/transfer-intents/drain"));
        var drained = await drain.Content.ReadFromJsonAsync<TransferIntentDrainResponse>();
        Assert.Single(drained!.Intents);
        Assert.Equal("uid-1", drained.Intents[0].PlayerUid);

        var empty = await host.Client.SendAsync(Signed(HttpMethod.Post, host.BaseUrl, "/api/transfer-intents/drain"));
        var second = await empty.Content.ReadFromJsonAsync<TransferIntentDrainResponse>();
        Assert.Empty(second!.Intents);
    }
}
