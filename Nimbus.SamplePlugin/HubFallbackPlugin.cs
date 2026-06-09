namespace Nimbus.SamplePlugin;

using Nimbus.Proxy;

public sealed class HubFallbackPlugin : IPlugin
{
    private const string PluginName = "hub-fallback";

    public string Name => PluginName;
    public string Version => "0.1.0";

    public void Initialize(IProxyApi api)
    {
        api.Events.Subscribe<PlayerChooseInitialServerEvent>(async evt =>
        {
            if (evt.Target != null) return;

            var hub = await api.ResolveServerAsync("hub", CancellationToken.None).ConfigureAwait(false);
            if (hub == null)
            {
                api.LogWarn(PluginName, "hub backend was not found, leaving routing unchanged");
                return;
            }

            evt.Target = hub;
            api.LogInfo(PluginName, $"routing {evt.Player.Name ?? evt.Player.ClientRemote} to hub");
        });
    }
}
