using System.Security.Cryptography;
using Nimbus.Shared;
using Nimbus.Shared.Models;

namespace Nimbus.Registry.Services;

// Why a mint was refused. The HTTP endpoint turns these into status codes and bodies, the
// in-proc client turns them into a null plus a log line: the presentation is the only thing
// the two modes are allowed to disagree about.
public enum ReservationMintStatus
{
    Ok,

    // No PlayerUid, no TargetServerId, or both.
    MissingSubject,

    // Nothing has heartbeat under that server id, so the reservation would be unconsumable.
    UnknownTarget,

    // A ban covers this (player, target) pair.
    Banned,
}

public readonly record struct ReservationMintResult(ReservationMintStatus Status, TransferReservation? Reservation)
{
    public bool Ok => Status == ReservationMintStatus.Ok;
}

// The inputs to a mint. They exist as a request object rather than a parameter list because the
// two callers read them from different places: the HTTP endpoint takes the TTL from the request
// body and the source id from the calling backend, the in-proc client takes both from proxy
// config.
public sealed class ReservationMintRequest
{
    public string PlayerUid { get; set; } = "";
    public string PlayerName { get; set; } = "";
    public string SourceServerId { get; set; } = "";
    public string TargetServerId { get; set; } = "";

    // 0 or less falls back to NimbusProtocol.DefaultReservationTtlSeconds.
    public int TtlSeconds { get; set; }

    // Ceiling applied after that fallback. 0 or less means no ceiling.
    public int MaxTtlSeconds { get; set; }

    public string? Reason { get; set; }
    public string RealRemoteIp { get; set; } = "";
    public int RealRemotePort { get; set; }
    public string ClientTransferId { get; set; } = "";
}

// The reservation-mint rules, held once because they are enforced twice. POST /api/reservations
// and the proxy's in-process client both come through here, so a rule added to this class is
// live in remote and embedded deployments by construction rather than by somebody remembering
// to write it down twice (#65).
public sealed class ReservationService
{
    private readonly BackendRegistry _backends;
    private readonly ReservationStore _reservations;
    private readonly BanStore _bans;
    private readonly TimeProvider _clock;

    public ReservationService(BackendRegistry backends, ReservationStore reservations, BanStore bans,
        TimeProvider? clock = null)
    {
        _backends = backends;
        _reservations = reservations;
        _bans = bans;
        _clock = clock ?? TimeProvider.System;
    }

    // Mints and stores the reservation, or says why it refused. Nothing is stored on refusal:
    // an unconsumable reservation would sit in the store until the sweeper noticed it.
    public ReservationMintResult Mint(ReservationMintRequest req)
    {
        if (req is null || string.IsNullOrEmpty(req.PlayerUid) || string.IsNullOrEmpty(req.TargetServerId))
            return new ReservationMintResult(ReservationMintStatus.MissingSubject, null);

        if (_backends.Get(req.TargetServerId) is null)
            return new ReservationMintResult(ReservationMintStatus.UnknownTarget, null);

        // Bans are enforced at the proxy, which knows the player and the destination at the
        // same time. This is the multi-proxy backstop: a proxy running on a ban list that is
        // seconds out of date still cannot mint the reservation that would seat the player.
        if (_bans.FindBlocking(req.PlayerUid, req.TargetServerId) is not null)
            return new ReservationMintResult(ReservationMintStatus.Banned, null);

        // Whitelists get no equivalent backstop: whether coverage is required at all lives in
        // proxy config, and the registry has no way to know which mode a proxy is running.

        int ttl = req.TtlSeconds;
        if (ttl <= 0) ttl = NimbusProtocol.DefaultReservationTtlSeconds;
        if (req.MaxTtlSeconds > 0 && ttl > req.MaxTtlSeconds) ttl = req.MaxTtlSeconds;

        // The injected clock, never DateTimeOffset.UtcNow: ReservationStore judges expiry on
        // this same clock, and stamping from another one puts the mint and the read of it on
        // two different timelines.
        long now = _clock.GetUtcNow().ToUnixTimeSeconds();
        var reservation = new TransferReservation
        {
            Id = NewId(),
            PlayerUid = req.PlayerUid,
            PlayerName = req.PlayerName ?? "",
            SourceServerId = req.SourceServerId ?? "",
            TargetServerId = req.TargetServerId,
            ExpiresAtUnix = now + ttl,
            Reason = req.Reason,
            RealRemoteIp = req.RealRemoteIp ?? "",
            RealRemotePort = req.RealRemotePort,
            ClientTransferId = req.ClientTransferId ?? "",
        };
        _reservations.Add(reservation);
        return new ReservationMintResult(ReservationMintStatus.Ok, reservation);
    }

    // 96 bits from the CSPRNG. Ids key the store, so a collision would have one mint overwrite
    // another and leave a player holding a ticket that was already consumed.
    private static string NewId()
    {
        Span<byte> bytes = stackalloc byte[12];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes);
    }
}
