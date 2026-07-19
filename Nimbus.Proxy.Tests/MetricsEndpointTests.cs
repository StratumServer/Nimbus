using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Xunit;

namespace Nimbus.Proxy.Tests;

/// <summary>
/// Boots the metrics host on an ephemeral loopback port and exercises /status over real
/// HTTP: payload shape (camelCase JSON, the contract panels code against), the optional
/// bearer/query token gate, and the status_api kill switch.
/// </summary>
public class MetricsEndpointTests
{
    private sealed class Host : IAsyncDisposable
    {
        public required WebApplication App { get; init; }
        public required string BaseUrl { get; init; }
        public HttpClient Client { get; } = new();

        public static async Task<Host> StartAsync(MetricsConfig cfg, bool withProvider = true)
        {
            var proxyCfg = new ProxyConfig { Servers = new() { ["hub"] = "10.0.0.1:42421" }, Try = new() };
            var router = new BackendRouter(proxyCfg, registry: null);
            long start = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            var endpoint = new MetricsEndpoint(cfg, CancellationToken.None, withProvider
                ? ct => StatusReport.BuildAsync(proxyCfg, router, null, start, ct)
                : null);
            var app = endpoint.BuildApp("http://127.0.0.1:0");
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

    [Fact]
    public async Task Status_ServesTheCamelCaseJsonContract()
    {
        await using var host = await Host.StartAsync(new MetricsConfig());

        var resp = await host.Client.GetAsync(host.BaseUrl + "/status");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("0.1.0-dev", doc.RootElement.GetProperty("proxy").GetProperty("version").GetString());
        var backend = doc.RootElement.GetProperty("backends").EnumerateArray().Single();
        Assert.Equal("hub", backend.GetProperty("serverId").GetString());
        Assert.False(backend.GetProperty("registered").GetBoolean());
        Assert.True(doc.RootElement.TryGetProperty("totals", out _));
    }

    [Fact]
    public async Task Status_WithAToken_RejectsAnonymous_AcceptsHeaderAndQuery()
    {
        await using var host = await Host.StartAsync(new MetricsConfig { StatusApiToken = "panel-secret" });

        var anon = await host.Client.GetAsync(host.BaseUrl + "/status");
        Assert.Equal(HttpStatusCode.Unauthorized, anon.StatusCode);

        var wrong = new HttpRequestMessage(HttpMethod.Get, host.BaseUrl + "/status");
        wrong.Headers.Add("Authorization", "Bearer nope");
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Client.SendAsync(wrong)).StatusCode);

        var header = new HttpRequestMessage(HttpMethod.Get, host.BaseUrl + "/status");
        header.Headers.Add("Authorization", "Bearer panel-secret");
        Assert.Equal(HttpStatusCode.OK, (await host.Client.SendAsync(header)).StatusCode);

        var query = await host.Client.GetAsync(host.BaseUrl + "/status?token=panel-secret");
        Assert.Equal(HttpStatusCode.OK, query.StatusCode);
    }

    [Fact]
    public async Task Status_CanBeDisabled_MetricsAndHealthStayUp()
    {
        await using var host = await Host.StartAsync(new MetricsConfig { StatusApi = false });

        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync(host.BaseUrl + "/status")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync(host.BaseUrl + "/health")).StatusCode);

        var metrics = await host.Client.GetAsync(host.BaseUrl + "/metrics");
        Assert.Equal(HttpStatusCode.OK, metrics.StatusCode);
        Assert.Contains("nimbus_", await metrics.Content.ReadAsStringAsync());
    }
}
