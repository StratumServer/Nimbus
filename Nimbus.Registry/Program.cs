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

        // Asked before anything writes: both files it looks at are files the loader below is
        // about to create or migrate.
        bool firstRun = RegistryFirstRun.IsFirstRun(configPath);
        bool generated = false;
        string? writeError = null;
        if (firstRun) generated = RegistryFirstRun.TryWriteConfig(configPath, out writeError);

        RegistryConfig cfg;
        try
        {
            cfg = TomlConfig.LoadOrCreate<RegistryConfig>(configPath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Nimbus] failed to load config '{configPath}': {ex.Message}");
            return 2;
        }

        if (firstRun)
        {
            // The previous wording of this line was never printed: it asked whether the config
            // file was missing, one statement after LoadOrCreate had written it.
            Console.WriteLine($"[Nimbus] no config at {configPath}, wrote defaults");
            if (generated)
                Console.WriteLine("[Nimbus] shared_secret was generated for this install; copy it into \"SharedSecret\" in each backend's nimbus-server.json and into registry.shared_secret in each remote-mode proxy's nimbus.proxy.toml");
            else
                Console.WriteLine($"[Nimbus] WARNING: could not generate a shared_secret for the new config ({writeError}); edit {configPath} before going live");
        }

        // Still reached on a config an operator wrote by hand, on the panel eggs' template, and on
        // a first run whose write failed. The standalone registry warns where the proxy refuses,
        // because the proxy is refusing to expose an embedded registry it also owns, while this
        // process is the registry: a refusal here takes the whole network down rather than keeping
        // it from opening.
        if (SecretPlaceholders.IsPlaceholder(cfg.SharedSecret))
        {
            Console.WriteLine($"[Nimbus] WARNING: shared_secret is still a placeholder. Heartbeats will be open to anyone who can reach {cfg.BindUrl}. Put the same random value here and in every backend's \"SharedSecret\" before going live.");
        }

        foreach (var complaint in RegistryConfigWarnings.ApiTokens(cfg))
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
}
