namespace Nimbus.ServerMod;

using System.Net.Http.Json;
using System.Text.Json;
using Nimbus.Shared;
using Nimbus.Shared.Models;
using Nimbus.Shared.Security;

internal sealed class NimbusRegistryClient : IDisposable
{
    private readonly NimbusServerConfig cfg;
    private readonly HttpClient http;

    public NimbusRegistryClient(NimbusServerConfig cfg)
    {
        this.cfg = cfg;
        http = new HttpClient
        {
            BaseAddress = new Uri(cfg.RegistryUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(cfg.RegistryHttpTimeoutSeconds)
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"Nimbus-ServerMod/{NimbusProtocol.NimbusVersion}");
    }

    public Task<BackendHeartbeatResponse?> HeartbeatAsync(BackendHeartbeat heartbeat, CancellationToken ct)
        => PostJsonAsync<BackendHeartbeatResponse>("api/heartbeat", heartbeat, ct);

    public Task<NetworkSnapshot?> GetServersAsync(CancellationToken ct)
        => GetJsonAsync<NetworkSnapshot>("api/servers", ct);

    public Task<TransferIntentResponse?> PostTransferIntentAsync(TransferIntentRequest request, CancellationToken ct)
        => PostJsonAsync<TransferIntentResponse>("api/transfer-intents", request, ct);

    // Returns the full reservation (including real client IP/port) on success, null if none found.
    public async Task<TransferReservation?> ConsumeReservationByUidAsync(string playerUid, string serverId, CancellationToken ct)
    {
        var path = $"api/reservations/consume-by-uid?uid={Uri.EscapeDataString(playerUid)}&target={Uri.EscapeDataString(serverId)}";
        var result = await PostNoBodyAsync<ReservationResponse>(path, ct).ConfigureAwait(false);
        return result?.Ok == true ? result.Reservation : null;
    }

    private async Task<T?> PostNoBodyAsync<T>(string path, CancellationToken ct) where T : class
    {
        using var msg = new HttpRequestMessage(HttpMethod.Post, path);
        ApplySignedHeaders(msg, "POST", "/" + path, Array.Empty<byte>());
        using var resp = await http.SendAsync(msg, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<T>(cancellationToken: ct).ConfigureAwait(false);
    }

    private async Task<T?> PostJsonAsync<T>(string path, object body, CancellationToken ct) where T : class
    {
        byte[] bodyBytes = JsonSerializer.SerializeToUtf8Bytes(body);
        using var msg = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new ByteArrayContent(bodyBytes)
        };
        msg.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        ApplySignedHeaders(msg, "POST", "/" + path, bodyBytes);

        using var resp = await http.SendAsync(msg, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<T>(cancellationToken: ct).ConfigureAwait(false);
    }

    private async Task<T?> GetJsonAsync<T>(string path, CancellationToken ct) where T : class
    {
        using var msg = new HttpRequestMessage(HttpMethod.Get, path);
        ApplySignedHeaders(msg, "GET", "/" + path, Array.Empty<byte>());

        using var resp = await http.SendAsync(msg, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<T>(cancellationToken: ct).ConfigureAwait(false);
    }

    private void ApplySignedHeaders(HttpRequestMessage msg, string method, string canonicalPath, byte[] body)
    {
        long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string nonce = HmacSigner.NewNonce();
        int q = canonicalPath.IndexOf('?');
        string pathForSig = q >= 0 ? canonicalPath[..q] : canonicalPath;
        string canonical = HmacSigner.CanonicalString(method, pathForSig, NimbusProtocol.ProtocolVersion, ts, nonce, body);
        string sig = HmacSigner.Sign(cfg.SharedSecret, canonical);
        msg.Headers.Add(NimbusProtocol.SignatureHeader, sig);
        msg.Headers.Add(NimbusProtocol.TimestampHeader, ts.ToString(System.Globalization.CultureInfo.InvariantCulture));
        msg.Headers.Add(NimbusProtocol.NonceHeader, nonce);
        msg.Headers.Add(NimbusProtocol.ProtocolHeader, NimbusProtocol.ProtocolVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    public void Dispose()
    {
        http.Dispose();
    }

}
