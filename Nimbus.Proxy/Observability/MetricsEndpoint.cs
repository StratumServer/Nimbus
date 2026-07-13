using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Nimbus.Proxy;

internal sealed class MetricsEndpoint
{
    private readonly MetricsConfig cfg;
    private readonly CancellationToken stopToken;
    private readonly Func<CancellationToken, Task<StatusReport>>? statusProvider;

    public MetricsEndpoint(MetricsConfig cfg, CancellationToken stopToken,
        Func<CancellationToken, Task<StatusReport>>? statusProvider = null)
    {
        this.cfg = cfg;
        this.stopToken = stopToken;
        this.statusProvider = statusProvider;
    }

    public Task RunAsync()
    {
        if (!cfg.Enabled)
        {
            Log.Info("metrics endpoint disabled (metrics.enabled = false)");
            return Task.CompletedTask;
        }

        var app = BuildApp(cfg.Bind);
        Log.Info($"metrics listening on {cfg.Bind}{cfg.Path}"
            + (cfg.StatusApi && statusProvider != null ? " (+ /status)" : ""));
        return HostingAbstractionsHostExtensions.RunAsync(app, stopToken);
    }

    // Separated from RunAsync so tests can bind an ephemeral port and drive the app directly.
    internal WebApplication BuildApp(string bind)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls(bind);
        builder.Logging.ClearProviders();
        builder.Logging.AddFilter((_, level) => level >= LogLevel.Error);
        var app = builder.Build();

        app.MapGet(cfg.Path, () => Results.Text(ProxyMetrics.RenderPrometheus(), "text/plain"));
        app.MapGet("/health", () => Results.Ok(new { ok = true }));

        if (cfg.StatusApi && statusProvider != null)
        {
            app.MapGet("/status", async (HttpContext ctx, CancellationToken ct) =>
            {
                if (!IsAuthorized(ctx))
                    return Results.Unauthorized();
                return Results.Ok(await statusProvider(ct).ConfigureAwait(false));
            });
        }

        return app;
    }

    // Empty token = open endpoint (the default bind is loopback). With a token, accept the
    // Authorization header or ?token= for panels that cannot set headers.
    private bool IsAuthorized(HttpContext ctx)
    {
        string token = cfg.StatusApiToken;
        if (string.IsNullOrEmpty(token)) return true;

        string auth = ctx.Request.Headers.Authorization.ToString();
        if (auth == $"Bearer {token}") return true;
        return ctx.Request.Query["token"].ToString() == token;
    }
}
