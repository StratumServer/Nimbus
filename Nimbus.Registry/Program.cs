using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Nimbus.Registry;
using Nimbus.Shared;

namespace Nimbus.Registry;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        string configPath = args.Length > 0 ? args[0] : "nimbus.registry.toml";
        RegistryConfig cfg;
        try
        {
            cfg = TomlConfig.LoadOrCreate<RegistryConfig>(configPath);
            if (!File.Exists(configPath + ".bak") && !File.Exists(configPath))
                Console.WriteLine($"[Nimbus] wrote default config to {configPath}. Edit shared_secret before exposing publicly.");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Nimbus] failed to load config '{configPath}': {ex.Message}");
            return 2;
        }

        if (cfg.SharedSecret is "change-me-and-keep-secret" or "REPLACE_ME_WITH_A_LONG_RANDOM_STRING" or "")
        {
            Console.WriteLine("[Nimbus] WARNING: SharedSecret is still default. Heartbeats will be open to anyone who can hit this URL. Edit nimbus.registry.toml before going live.");
        }

        foreach (var complaint in ApiTokenWarnings(cfg))
            Console.WriteLine($"[Nimbus] WARNING: {complaint}");

        var builder = WebApplication.CreateBuilder(args);
        builder.WebHost.UseUrls(cfg.BindUrl);
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Logging.AddFilter("Microsoft", LogLevel.None);
        builder.Logging.AddFilter("System", LogLevel.None);
        builder.Logging.AddProvider(new RegistryConsoleLoggerProvider());
        builder.AddNimbusRegistry(cfg);

        var app = builder.Build();
        app.UseNimbusRegistry();

        Console.WriteLine($"[Nimbus] registry listening on {cfg.BindUrl}");
        Console.WriteLine($"[Nimbus] protocol={NimbusProtocol.ProtocolVersion} version={NimbusProtocol.NimbusVersion}");
        await app.RunAsync();
        return 0;
    }

    // The standalone registry has no config validator of its own, so the two [api_tokens]
    // settings that can silently do nothing say so here. ProxyConfigValidator carries the same
    // two checks for the embedded registry, worded the same way.
    internal static List<string> ApiTokenWarnings(RegistryConfig cfg)
    {
        var complaints = new List<string>();
        if (!cfg.ApiTokens.Enabled) return complaints;

        if (cfg.ApiTokens.RateLimitPerMinute <= 0)
            complaints.Add($"api_tokens.rate_limit_per_minute is {cfg.ApiTokens.RateLimitPerMinute}, which is not a rate. Falling back to 60 per token per minute.");

        if (!Uri.TryCreate(cfg.BindUrl, UriKind.Absolute, out var uri)) return complaints;
        // 0.0.0.0 is not loopback: it is every interface, loopback included, which is the bind
        // this warning exists for.
        if (uri.Scheme == "https" || uri.IsLoopback) return complaints;
        if (cfg.ApiTokens.TrustForwardedProto)
        {
            complaints.Add("api_tokens.trust_forwarded_proto = true makes the registry believe an X-Forwarded-Proto header on a plain-HTTP bind. Only set it when a TLS-terminating proxy is the sole route to bind_url.");
            return complaints;
        }
        complaints.Add("api_tokens.enabled = true on a non-loopback plain-HTTP bind_url: bearer auth will refuse every request, because a token is only as safe as the transport under it. Serve https, bind loopback, or set api_tokens.trust_forwarded_proto behind a TLS-terminating proxy.");
        return complaints;
    }
}
