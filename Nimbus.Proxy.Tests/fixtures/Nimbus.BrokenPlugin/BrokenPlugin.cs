namespace Nimbus.BrokenPlugin;

using Nimbus.Proxy;

/// <summary>
/// Abstract, so the loader has to walk past it without trying to construct it. Safe to keep in
/// the same assembly as the concrete plugin below: abstract types are skipped before the loader
/// claims the manifest id, so it never becomes a duplicate of anything.
/// </summary>
public abstract class PluginBase : IPlugin
{
    public abstract string Name { get; }

    public virtual void Initialize(IProxyApi api) { }
}

/// <summary>Throws out of Initialize, the way a plugin with a missing config file would.</summary>
public sealed class BrokenInitializePlugin : PluginBase
{
    public override string Name => "broken-init";

    public override void Initialize(IProxyApi api)
    {
        // Subscribes first, then fails. A plugin that got half way through registering itself is
        // the interesting case: the loader has to leave nothing of it behind.
        api.Events.Subscribe<PlayerConnectEvent>(evt => evt.Deny("denied by a plugin that failed to load"));
        throw new InvalidOperationException("plugin config file is missing");
    }
}
