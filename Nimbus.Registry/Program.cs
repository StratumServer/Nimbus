using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nimbus.Registry.MasterServer;
using Nimbus.Registry.Services;
using Nimbus.Shared;
using Nimbus.Shared.Models;

namespace Nimbus.Registry;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        string configPath = args.Length > 0 ? args[0] : "nimbus.registry.json";
        RegistryConfig cfg;
        try
        {
            if (!File.Exists(configPath))
            {
                cfg = new RegistryConfig();
                await File.WriteAllTextAsync(configPath,
                    JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine($"[Nimbus] wrote default config to {configPath}. Edit SharedSecret before exposing publicly.");
            }
            else
            {
                cfg = JsonSerializer.Deserialize<RegistryConfig>(await File.ReadAllTextAsync(configPath))
                      ?? new RegistryConfig();
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Nimbus] failed to load config '{configPath}': {ex.Message}");
            return 2;
        }

        if (cfg.SharedSecret is "change-me-and-keep-secret" or "REPLACE_ME_WITH_A_LONG_RANDOM_STRING" or "")
        {
            Console.WriteLine("[Nimbus] WARNING: SharedSecret is still default. Heartbeats will be open to anyone who can hit this URL. Edit nimbus.registry.json before going live.");
        }

        var builder = WebApplication.CreateBuilder(args);
        builder.WebHost.UseUrls(cfg.BindUrl);
        builder.Services.AddSingleton(cfg);
        builder.Services.AddSingleton<BackendRegistry>();
        builder.Services.AddSingleton<ReservationStore>();
        builder.Services.AddSingleton<TransferIntentStore>();
        builder.Services.AddSingleton<NonceCache>();
        builder.Services.AddHostedService<RegistrySweeper>();
        builder.Services.AddHostedService<MasterServerBroadcaster>();

        var app = builder.Build();

        app.UseMiddleware<HmacAuthMiddleware>();
        Endpoints.Map(app);

        Console.WriteLine($"[Nimbus] registry listening on {cfg.BindUrl}");
        Console.WriteLine($"[Nimbus] protocol={NimbusProtocol.ProtocolVersion} version={NimbusProtocol.NimbusVersion}");
        await app.RunAsync();
        return 0;
    }
}

// Background sweep: prune stale backends, expired reservations, and old nonces.
public sealed class RegistrySweeper : BackgroundService
{
    private readonly BackendRegistry _backends;
    private readonly ReservationStore _reservations;
    private readonly TransferIntentStore _intents;
    private readonly NonceCache _nonces;
    private readonly ILogger<RegistrySweeper> _log;

    public RegistrySweeper(BackendRegistry b, ReservationStore r, TransferIntentStore i, NonceCache n, ILogger<RegistrySweeper> log)
    { _backends = b; _reservations = r; _intents = i; _nonces = n; _log = log; }

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
                if (b + r + i + n > 0)
                    _log.LogDebug("sweep: dropped backends={B} reservations={R} intents={I} nonces={N}", b, r, i, n);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "sweep failed");
            }
            try { await Task.Delay(period, stop); } catch (TaskCanceledException) { }
        }
    }
}
