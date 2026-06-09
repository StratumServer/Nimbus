namespace Nimbus.ServerMod;

using ProtoBuf;

[ProtoContract(ImplicitFields = ImplicitFields.AllPublic)]
public sealed class NimbusClientHello
{
    public int ProtocolVersion { get; set; }
    public bool SupportsSeamlessTransfers { get; set; }
}

[ProtoContract(ImplicitFields = ImplicitFields.AllPublic)]
public sealed class NimbusSeamlessPrepare
{
    public string TransferId { get; set; } = "";
    public string TargetServerId { get; set; } = "";
    public string Reason { get; set; } = "";
}

[ProtoContract(ImplicitFields = ImplicitFields.AllPublic)]
public sealed class NimbusSeamlessCommit
{
    public string TransferId { get; set; } = "";
}

[ProtoContract(ImplicitFields = ImplicitFields.AllPublic)]
public sealed class NimbusSeamlessReady
{
    public string TransferId { get; set; } = "";
}

[ProtoContract(ImplicitFields = ImplicitFields.AllPublic)]
public sealed class NimbusSeamlessAbort
{
    public string TransferId { get; set; } = "";
    public string Message { get; set; } = "";
}
