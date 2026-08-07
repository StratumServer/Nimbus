using Nimbus.Shared;

namespace Nimbus.ServerMod;

public sealed class NimbusServerConfig
{
    public bool Enabled { get; set; } = true;
    public string ServerId { get; set; } = "";
    public string DisplayName { get; set; } = "";

    // Address this backend advertises to the registry: where the NETWORK reaches this
    // server, not where players connect (players connect to the proxy's bind). The proxy
    // dials it for seamless transfers and stamps it into redirect packets; it must be
    // reachable from the proxy, and today's RedirectFix clients reconnect to the proxy's
    // cached address regardless. See the README's "Addresses" section for the full map.
    public string PublicHost { get; set; } = "";
    public int PublicPort { get; set; } = 42420;
    public List<string> Tags { get; set; } = new();

    public string RegistryUrl { get; set; } = "";

    // The HMAC key this backend authenticates to the registry with. It is never generated here:
    // the proxy or the standalone registry generates one on its first run and this end has to
    // match it, so a value minted on the backend could only ever be a value that fails to
    // authenticate. What the default does instead is name the file to copy it out of, since a
    // freshly written nimbus-server.json is the one place an operator is looking. Every value in
    // SecretPlaceholders, this one included, counts as unset: the mod stays "misconfigured"
    // rather than heartbeating with a string published in the Nimbus repository.
    public string SharedSecret { get; set; } = SecretPlaceholders.BackendConfig;
    public int HeartbeatIntervalSeconds { get; set; } = 5;
    public int RegistryHttpTimeoutSeconds { get; set; } = 5;

    public bool Maintenance { get; set; } = false;
    public bool ReservationRequired { get; set; } = true;

    // Behavior when ReservationRequired is on but the registry can't be reached to confirm a
    // reservation. Default (false) fails open: the player is let in, so a registry outage does
    // not lock everyone out. Set true to fail closed: the player is kicked, so a registry
    // outage cannot be used to bypass the proxy on a reservation-gated network.
    public bool FailClosedWhenRegistryUnreachable { get; set; } = false;

    public bool AllowPlayerServerCommand { get; set; } = true;
    public string TransferMode { get; set; } = "redirect";
    public int SeamlessPrepareAckTimeoutSeconds { get; set; } = 8;

    // Named shortcuts for /server, so players type /hub instead of /server hub. Empty by
    // default: a network defines the ones its layout actually has.
    public List<ShortcutCommand> ShortcutCommands { get; set; } = new();

    public void Normalize()
    {
        Tags ??= new List<string>();
        ShortcutCommands ??= new List<ShortcutCommand>();
        foreach (var shortcut in ShortcutCommands) shortcut.Normalize();
        if (PublicPort <= 0 || PublicPort > 65535) PublicPort = 42420;
        if (HeartbeatIntervalSeconds < 1) HeartbeatIntervalSeconds = 5;
        if (RegistryHttpTimeoutSeconds < 1) RegistryHttpTimeoutSeconds = 5;
        if (SeamlessPrepareAckTimeoutSeconds < 1) SeamlessPrepareAckTimeoutSeconds = 1;
        if (SeamlessPrepareAckTimeoutSeconds > 30) SeamlessPrepareAckTimeoutSeconds = 30;
        if (string.IsNullOrWhiteSpace(TransferMode)) TransferMode = "redirect";
    }

    public string StatusSummary()
    {
        if (!Enabled) return "disabled";
        return $"enabled id={ServerId} public={PublicHost}:{PublicPort} transferMode={TransferMode} registry={(string.IsNullOrWhiteSpace(RegistryUrl) ? "<unset>" : RegistryUrl)}";
    }
}

// One shortcut command, for example /hub or /creative.
//
// Targets is a fallback chain, tried in order, which is what makes a "/lobby" that means
// "this gamemode's lobby, or the hub if it has none" expressible: ["survival-lobby", "hub"].
// The first target that is registered, healthy and not this server wins, so a shortcut also
// degrades gracefully when part of the network is down.
public sealed class ShortcutCommand
{
    // Command word without the slash.
    public string Name { get; set; } = "";

    public List<string> Targets { get; set; } = new();

    // Vintage Story privilege required to run it, for example "chat" for everyone or
    // "controlserver" to keep /staff to admins.
    public string Privilege { get; set; } = "chat";

    // Shown in the in-game command list. Generated from the targets when empty.
    public string Description { get; set; } = "";

    public void Normalize()
    {
        Name = (Name ?? "").Trim().TrimStart('/');
        Targets ??= new List<string>();
        Targets.RemoveAll(string.IsNullOrWhiteSpace);
        if (string.IsNullOrWhiteSpace(Privilege)) Privilege = "chat";
        if (string.IsNullOrWhiteSpace(Description))
            Description = Targets.Count > 0 ? $"Move yourself to {Targets[0]}" : "Move yourself to another server";
    }

    public bool IsUsable() => !string.IsNullOrWhiteSpace(Name) && Targets.Count > 0;
}
