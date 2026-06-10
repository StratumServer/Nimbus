namespace Nimbus.ServerMod;

public sealed class NimbusServerConfig
{
    public bool Enabled { get; set; } = true;
    public string ServerId { get; set; } = "";
    public string DisplayName { get; set; } = "";
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
