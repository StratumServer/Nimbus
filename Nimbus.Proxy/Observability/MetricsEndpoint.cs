using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;

namespace Nimbus.Proxy;

internal sealed class MetricsEndpoint
{
    private readonly MetricsConfig cfg;
    private readonly CancellationToken stopToken;

    public MetricsEndpoint(MetricsConfig cfg, CancellationToken stopToken)
    {
        this.cfg = cfg;
        this.stopToken = stopToken;
    }

    public Task RunAsync()
    {
        if (!cfg.Enabled)
        {
            Log.Info("metrics endpoint disabled (metrics.enabled = false)");
            return Task.CompletedTask;
        }

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls(cfg.Bind);
        var app = builder.Build();

        app.MapGet(cfg.Path, () => Results.Text(ProxyMetrics.RenderPrometheus(), "text/plain"));
        app.MapGet("/health", () => Results.Ok(new { ok = true }));

        Log.Info($"metrics listening on {cfg.Bind}{cfg.Path}");
        return HostingAbstractionsHostExtensions.RunAsync(app, stopToken);
    }
}
