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
    public string SharedSecret { get; set; } = "";
    public int HeartbeatIntervalSeconds { get; set; } = 5;
    public int RegistryHttpTimeoutSeconds { get; set; } = 5;

    public bool Maintenance { get; set; } = false;
    public bool ReservationRequired { get; set; } = true;
    public bool AllowPlayerServerCommand { get; set; } = true;
    public string TransferMode { get; set; } = "redirect";
    public int SeamlessPrepareAckTimeoutSeconds { get; set; } = 8;

    public void Normalize()
    {
        Tags ??= new List<string>();
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
