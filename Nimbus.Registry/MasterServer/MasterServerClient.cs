using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Nimbus.Registry.MasterServer;

// POSTs JSON to register/heartbeat/unregister. Response is always {status, data}.
internal sealed class MasterServerClient
{
    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNamingPolicy = null,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never
    };

    private readonly string _baseUrl;
    private readonly ILogger _log;

    public MasterServerClient(string baseUrl, ILogger log)
    {
        _baseUrl = baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/";
        _log = log;
    }

    public Task<ResponsePacket?> RegisterAsync(RegisterRequestPacket packet, CancellationToken ct)
        => PostAsync("register", packet, ct);

    public Task<ResponsePacket?> HeartbeatAsync(HeartbeatPacket packet, CancellationToken ct)
        => PostAsync("heartbeat", packet, ct);

    public Task<ResponsePacket?> UnregisterAsync(UnregisterPacket packet, CancellationToken ct)
        => PostAsync("unregister", packet, ct);

    private async Task<ResponsePacket?> PostAsync<T>(string path, T body, CancellationToken ct)
    {
        var url = _baseUrl + path;
        try
        {
            var resp = await _http.PostAsJsonAsync(url, body, _json, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogWarning("master server {Path} returned HTTP {Code}", path, (int)resp.StatusCode);
                return new ResponsePacket { status = "timeout", data = $"HTTP {(int)resp.StatusCode}" };
            }
            return await resp.Content.ReadFromJsonAsync<ResponsePacket>(_json, ct);
        }
        catch (TaskCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "master server {Path} failed", path);
            return null;
        }
    }
}
