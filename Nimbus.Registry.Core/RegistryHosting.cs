using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nimbus.Registry.MasterServer;
using Nimbus.Registry.Services;

namespace Nimbus.Registry;

// Wires the registry services (BackendRegistry, ReservationStore, TransferIntentStore,
// NonceCache, sweeper, optional master-server broadcaster) into a WebApplicationBuilder,
// and maps the HMAC-authed /api/* endpoints. Used by the standalone Nimbus.Registry exe
// and by Nimbus.Proxy's embedded registry mode (single-process deployments).
public static class RegistryHosting
{
    public static void AddNimbusRegistry(this WebApplicationBuilder builder, RegistryConfig cfg, bool withMasterServer = true)
    {
        builder.Services.AddSingleton(cfg);
        // TryAdd, not Add: a host that embeds the registry may register its own clock
        // before calling this, and the last registration would otherwise win and drop it.
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<BackendRegistry>();
        builder.Services.AddSingleton<ReservationStore>();
        builder.Services.AddSingleton<TransferIntentStore>();
        builder.Services.AddSingleton<NonceCache>();
        builder.Services.AddSingleton<BanStore>();
        builder.Services.AddSingleton<WhitelistStore>();
        builder.Services.AddHostedService<RegistrySweeper>();
        if (withMasterServer) builder.Services.AddHostedService<MasterServerBroadcaster>();
    }

    public static void UseNimbusRegistry(this WebApplication app)
    {
        app.UseMiddleware<HmacAuthMiddleware>();
        Endpoints.Map(app);
    }
}

// Background sweep: prune stale backends, expired reservations, and old nonces.
public sealed class RegistrySweeper : BackgroundService
{
    private readonly BackendRegistry _backends;
    private readonly ReservationStore _reservations;
    private readonly TransferIntentStore _intents;
    private readonly NonceCache _nonces;
    private readonly BanStore _bans;
    private readonly WhitelistStore _whitelist;
    private readonly ILogger<RegistrySweeper> _log;

    public RegistrySweeper(BackendRegistry b, ReservationStore r, TransferIntentStore i, NonceCache n, BanStore bans,
        WhitelistStore whitelist, ILogger<RegistrySweeper> log)
    { _backends = b; _reservations = r; _intents = i; _nonces = n; _bans = bans; _whitelist = whitelist; _log = log; }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        var period = TimeSpan.FromSeconds(15);
        while (!stop.IsCancellationRequested)
        {
            try
            {
                int b = _backends.Prune();
                int r = _reservations.Prune();
                int i = _intents.Prune();
                int n = _nonces.Prune();
                int x = _bans.Prune();
                int w = _whitelist.Prune();
                if (b + r + i + n + x + w > 0)
                    _log.LogDebug("sweep: dropped backends={B} reservations={R} intents={I} nonces={N} bans={X} whitelist={W}", b, r, i, n, x, w);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "sweep failed");
            }
            try { await Task.Delay(period, stop); } catch (TaskCanceledException) { }
        }
    }
}
