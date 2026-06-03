using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Nimbus.Registry;
using Nimbus.Registry.Services;

namespace Nimbus.Proxy;

internal sealed class ProxyRegistryHost : IAsyncDisposable
{
    private readonly WebApplication? embeddedHost;

    private ProxyRegistryHost(IRegistryClient? client, WebApplication? embeddedHost)
    {
        Client = client;
        this.embeddedHost = embeddedHost;
    }

    public IRegistryClient? Client { get; }

    public static ProxyRegistryHost Build(ProxyConfig cfg, CancellationToken stopToken)
    {
        var mode = (cfg.Registry.Mode ?? "disabled").Trim().ToLowerInvariant();
        switch (mode)
        {
            case "disabled":
            case "":
                Log.Info("registry: disabled");
                return new ProxyRegistryHost(null, null);

            case "remote":
                if (string.IsNullOrWhiteSpace(cfg.Registry.Url) || string.IsNullOrWhiteSpace(cfg.Registry.SharedSecret))
                {
                    Log.Warn("registry.mode = remote but url/shared_secret is unset; treating as disabled");
                    return new ProxyRegistryHost(null, null);
                }
                Log.Info($"registry: remote url={cfg.Registry.Url} proxy_id={cfg.Registry.ProxyId} fail_on_error={cfg.Registry.FailOnError}");
                return new ProxyRegistryHost(new HttpRegistryClient(cfg.Registry), null);

            case "embedded":
                return BuildEmbedded(cfg, stopToken);

            default:
                Log.Warn($"registry.mode = '{mode}' is not recognized; treating as disabled");
                return new ProxyRegistryHost(null, null);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (embeddedHost != null)
        {
            using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try { await embeddedHost.StopAsync(stopCts.Token).ConfigureAwait(false); } catch { }
            try { await embeddedHost.DisposeAsync().ConfigureAwait(false); } catch { }
        }

        if (Client is IDisposable disposable)
            disposable.Dispose();
    }

    private static ProxyRegistryHost BuildEmbedded(ProxyConfig cfg, CancellationToken stopToken)
    {
        var coreCfg = new Nimbus.Registry.RegistryConfig
        {
            BindUrl = cfg.Registry.EmbeddedBind,
            SharedSecret = cfg.Registry.EmbeddedSharedSecret,
            BackendStaleSeconds = cfg.Registry.BackendStaleSeconds,
            BackendDropSeconds = cfg.Registry.BackendDropSeconds,
            NonceWindowSeconds = cfg.Registry.NonceWindowSeconds,
            MaxReservationTtlSeconds = cfg.Registry.MaxReservationTtlSeconds,
            LogHeartbeats = false,
        };
        coreCfg.Identity.AdvertiseOnMasterServer = cfg.Registry.AdvertiseOnMasterServer;

        if (!string.IsNullOrWhiteSpace(cfg.Registry.EmbeddedBind))
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls(cfg.Registry.EmbeddedBind);
            builder.AddNimbusRegistry(coreCfg, withMasterServer: cfg.Registry.AdvertiseOnMasterServer);

            var app = builder.Build();
            app.UseNimbusRegistry();
            _ = HostingAbstractionsHostExtensions.RunAsync(app, stopToken);
            Log.Info($"registry: embedded http bind={cfg.Registry.EmbeddedBind} proxy_id={cfg.Registry.ProxyId}");

            var backends = app.Services.GetRequiredService<BackendRegistry>();
            var reservations = app.Services.GetRequiredService<ReservationStore>();
            var intents = app.Services.GetRequiredService<TransferIntentStore>();
            return new ProxyRegistryHost(new InProcRegistryClient(backends, reservations, intents, cfg.Registry), app);
        }

        var backendsSvc = new BackendRegistry(coreCfg);
        var reservationsSvc = new ReservationStore();
        var intentsSvc = new TransferIntentStore();
        Log.Info($"registry: embedded (no http listener) proxy_id={cfg.Registry.ProxyId}");
        return new ProxyRegistryHost(new InProcRegistryClient(backendsSvc, reservationsSvc, intentsSvc, cfg.Registry), null);
    }
}
