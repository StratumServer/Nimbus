using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Nimbus.Shared;
using Nimbus.Shared.Models;
using Nimbus.Shared.Security;

namespace Nimbus.Proxy;

// Signed HTTP client for Nimbus.Registry. Used by the proxy to mint a short-lived transfer
// reservation immediately before a swap, so the target backend's identification gate accepts
// the player by UID instead of re-running Stratum auth (Stratum tokens are single-use and
// pinned to the original backend identity).
internal sealed class RegistryClient
{
    private readonly HttpClient http;
    private readonly NimbusConfig cfg;

    // Lightweight snapshot cache so admin commands and pre-swap lookups don't hammer the registry.
    private readonly object snapshotLock = new();
    private NetworkSnapshot? cachedSnapshot;
    private DateTimeOffset cachedSnapshotAt;
    private static readonly TimeSpan SnapshotTtl = TimeSpan.FromSeconds(3);

    public RegistryClient(NimbusConfig cfg)
    {
        this.cfg = cfg;
        http = new HttpClient
        {
            BaseAddress = new Uri(cfg.RegistryUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(Math.Max(1, cfg.RegistryHttpTimeoutSeconds))
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"Nimbus-Proxy/{NimbusProtocol.NimbusVersion}");
    }

    /// <summary>
    /// Ask the registry to mint a reservation for (playerUid -> targetServerId). Returns the
    /// reservation on success, null on any error (logged). Reason is recorded for audit only.
    /// </summary>
    public async Task<TransferReservation?> MintReservationAsync(
        string playerUid, string playerName, string targetServerId, string? reason, CancellationToken ct)
    {
        var req = new ReservationRequest
        {
            PlayerUid = playerUid,
            PlayerName = playerName ?? "",
            SourceServerId = cfg.ProxyServerId,
            TargetServerId = targetServerId,
            TtlSeconds = cfg.ReservationTtlSeconds,
            Reason = reason
        };

        try
        {
            byte[] body = JsonSerializer.SerializeToUtf8Bytes(req);
            const string path = "api/reservations";
            using var msg = new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = new ByteArrayContent(body)
            };
            msg.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            Sign(msg, "POST", "/" + path, body);

            using var resp = await http.SendAsync(msg, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                string detail = await SafeReadAsync(resp).ConfigureAwait(false);
                Log.Warn($"registry POST /{path} -> {(int)resp.StatusCode} {resp.ReasonPhrase} {detail}");
                return null;
            }

            var parsed = await resp.Content.ReadFromJsonAsync<ReservationResponse>(cancellationToken: ct).ConfigureAwait(false);
            if (parsed == null || !parsed.Ok || parsed.Reservation == null)
            {
                Log.Warn($"registry returned not-ok: {parsed?.Error ?? "<null>"}");
                return null;
            }
            return parsed.Reservation;
        }
        catch (Exception ex)
        {
            Log.Warn($"registry mint failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Returns a cached <see cref="NetworkSnapshot"/> (TTL = 3s) or fetches fresh on miss.
    /// Returns null on error (logged); callers should treat as "registry unavailable".
    /// </summary>
    public async Task<NetworkSnapshot?> GetServersAsync(CancellationToken ct, bool forceRefresh = false)
    {
        if (!forceRefresh)
        {
            lock (snapshotLock)
            {
                if (cachedSnapshot != null && DateTimeOffset.UtcNow - cachedSnapshotAt < SnapshotTtl)
                    return cachedSnapshot;
            }
        }

        try
        {
            const string path = "api/servers";
            using var msg = new HttpRequestMessage(HttpMethod.Get, path);
            Sign(msg, "GET", "/" + path, Array.Empty<byte>());
            using var resp = await http.SendAsync(msg, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                Log.Warn($"registry GET /{path} -> {(int)resp.StatusCode} {resp.ReasonPhrase}");
                return null;
            }
            var snap = await resp.Content.ReadFromJsonAsync<NetworkSnapshot>(cancellationToken: ct).ConfigureAwait(false);
            if (snap != null)
            {
                lock (snapshotLock) { cachedSnapshot = snap; cachedSnapshotAt = DateTimeOffset.UtcNow; }
            }
            return snap;
        }
        catch (Exception ex)
        {
            Log.Warn($"registry GET /api/servers failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Find a backend in the cached snapshot by ServerId (case-insensitive). Null if absent.</summary>
    public async Task<BackendSnapshot?> ResolveByServerIdAsync(string serverId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(serverId)) return null;
        var snap = await GetServersAsync(ct).ConfigureAwait(false);
        if (snap == null) return null;
        foreach (var b in snap.Backends)
            if (string.Equals(b.ServerId, serverId, StringComparison.OrdinalIgnoreCase))
                return b;
        return null;
    }

    private void Sign(HttpRequestMessage msg, string method, string canonicalPath, byte[] body)
    {
        long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string nonce = HmacSigner.NewNonce();
        int q = canonicalPath.IndexOf('?');
        string pathForSig = q >= 0 ? canonicalPath.Substring(0, q) : canonicalPath;
        string canonical = HmacSigner.CanonicalString(method, pathForSig, NimbusProtocol.ProtocolVersion, ts, nonce, body);
        string sig = HmacSigner.Sign(cfg.SharedSecret, canonical);
        msg.Headers.Add(NimbusProtocol.SignatureHeader, sig);
        msg.Headers.Add(NimbusProtocol.TimestampHeader, ts.ToString(System.Globalization.CultureInfo.InvariantCulture));
        msg.Headers.Add(NimbusProtocol.NonceHeader, nonce);
        msg.Headers.Add(NimbusProtocol.ProtocolHeader, NimbusProtocol.ProtocolVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private static async Task<string> SafeReadAsync(HttpResponseMessage resp)
    {
        try { return (await resp.Content.ReadAsStringAsync().ConfigureAwait(false)).Trim(); }
        catch { return ""; }
    }
}
