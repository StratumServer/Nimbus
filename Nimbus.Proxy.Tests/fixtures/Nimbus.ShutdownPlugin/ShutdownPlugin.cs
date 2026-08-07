namespace Nimbus.ShutdownPlugin;

using Nimbus.Proxy;

/// <summary>
/// Loads and subscribes like any other plugin, then throws out of Shutdown. Reload calls
/// ShutdownAll before it clears anything, so this is what stands between one bad plugin and an
/// operator's `nimctl reload` wedging the whole plugin set.
/// </summary>
public sealed class ShutdownThrowsPlugin : IPlugin
{
    public string Name => "shutdown-throws";
    public string Version => "2.0.0";

    public void Initialize(IProxyApi api)
        => api.Events.Subscribe<PlayerConnectEvent>(evt => evt.Deny("denied by shutdown-throws"));

    public void Shutdown() => throw new InvalidOperationException("shutdown blew up");
}
