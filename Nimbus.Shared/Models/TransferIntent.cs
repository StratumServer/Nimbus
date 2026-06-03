namespace Nimbus.Shared.Models;

// A queued transfer order. Backends post these to the registry when an admin (or the player
// themselves) asks to move to another server. The proxy drains the queue, looks up the live
// session by PlayerUid, and runs its existing swap path.
//
// Intents live in memory on the registry and expire quickly. The reservation that actually
// authorises the join is minted later by the proxy as part of the swap, so the real client
// IP gets attached at that point.
public sealed class TransferIntent
{
    public string Id { get; set; } = "";
    public string PlayerUid { get; set; } = "";
    public string PlayerName { get; set; } = "";
    public string SourceServerId { get; set; } = "";
    public string TargetServerId { get; set; } = "";

    // "redirect" by default. "seamless" needs the Nimbus mod and allow_seamless.
    // "splice" is accepted as a deprecated alias for "seamless".
    public string Mode { get; set; } = "redirect";

    public string? Reason { get; set; }
    public long ExpiresAtUnix { get; set; }

    // Who issued the request. Player UID for self-service /server, "admin:<name>" for /nimbus send,
    // or empty for system-driven transfers.
    public string RequestedBy { get; set; } = "";
}

public sealed class TransferIntentRequest
{
    public string PlayerUid { get; set; } = "";
    public string PlayerName { get; set; } = "";
    public string SourceServerId { get; set; } = "";
    public string TargetServerId { get; set; } = "";
    public string Mode { get; set; } = "redirect";
    public int TtlSeconds { get; set; } = 30;
    public string? Reason { get; set; }
    public string RequestedBy { get; set; } = "";
}

public sealed class TransferIntentResponse
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public TransferIntent? Intent { get; set; }
}

public sealed class TransferIntentDrainResponse
{
    public bool Ok { get; set; }
    public List<TransferIntent> Intents { get; set; } = new();
}
