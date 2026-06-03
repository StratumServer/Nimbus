using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
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

        var builder = WebApplication.CreateBuilder(args);
        builder.WebHost.UseUrls(cfg.BindUrl);
        builder.AddNimbusRegistry(cfg);

        var app = builder.Build();
        app.UseNimbusRegistry();

        Console.WriteLine($"[Nimbus] registry listening on {cfg.BindUrl}");
        Console.WriteLine($"[Nimbus] protocol={NimbusProtocol.ProtocolVersion} version={NimbusProtocol.NimbusVersion}");
        await app.RunAsync();
        return 0;
    }
}
