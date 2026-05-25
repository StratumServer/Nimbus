namespace Nimbus.Shared;

// Wire-format and capability versioning for Nimbus.
public static class NimbusProtocol
{
    public const int ProtocolVersion = 1;
    public const string NimbusVersion = "0.1.0-dev";

    public const string SignatureHeader = "X-Nimbus-Signature";
    public const string TimestampHeader = "X-Nimbus-Timestamp";
    public const string NonceHeader = "X-Nimbus-Nonce";
    public const string ProtocolHeader = "X-Nimbus-Protocol";

    // Max allowed clock skew between sender and registry, in seconds. Requests outside this
    // window are rejected regardless of signature validity.
    public const int MaxClockSkewSeconds = 30;

    public const int DefaultReservationTtlSeconds = 60;
}
