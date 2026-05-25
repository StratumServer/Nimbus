namespace Nimbus.Shared.Models;

// Short-lived signed permission for a single player to join a specific backend.
// Minted by the registry, consumed by the target backend on identification.
public sealed class TransferReservation
{
    public string Id { get; set; } = "";
    public string PlayerUid { get; set; } = "";
    public string PlayerName { get; set; } = "";
    public string SourceServerId { get; set; } = "";
    public string TargetServerId { get; set; } = "";
    public long ExpiresAtUnix { get; set; }
    public string? Reason { get; set; }
}

public sealed class ReservationRequest
{
    public string PlayerUid { get; set; } = "";
    public string PlayerName { get; set; } = "";
    public string SourceServerId { get; set; } = "";
    public string TargetServerId { get; set; } = "";
    public int TtlSeconds { get; set; } = NimbusProtocol.DefaultReservationTtlSeconds;
    public string? Reason { get; set; }
}

public sealed class ReservationResponse
{
    public bool Ok { get; set; }
    public TransferReservation? Reservation { get; set; }
    public string? Error { get; set; }
}
