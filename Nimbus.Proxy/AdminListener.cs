using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Nimbus.Proxy;

// Line-based JSON admin endpoint. See nimctl --help (Nimbus.Cli) for the full command list.
// Binds to localhost by default. No auth or TLS, this is the admin plane only.
internal sealed class AdminListener
{
    private readonly ProxyConfig cfg;
    private readonly ProxyListener proxy;
    private readonly CancellationToken stopToken;

    public AdminListener(ProxyConfig cfg, ProxyListener proxy, CancellationToken stopToken)
    {
        this.cfg = cfg;
        this.proxy = proxy;
        this.stopToken = stopToken;
    }

    public async Task RunAsync()
    {
        if (cfg.AdminPort <= 0)
        {
            Log.Info("admin endpoint disabled (AdminPort <= 0)");
            return;
        }
        var bindAddr = IPAddress.Parse(cfg.AdminHost);
        var listener = new TcpListener(bindAddr, cfg.AdminPort);
        listener.Start();
        Log.Info($"admin listening on {bindAddr}:{cfg.AdminPort}");

        try
        {
            while (!stopToken.IsCancellationRequested)
            {
                TcpClient c;
                try { c = await listener.AcceptTcpClientAsync(stopToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                _ = Task.Run(() => HandleAsync(c), stopToken);
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        var remote = client.Client.RemoteEndPoint?.ToString() ?? "?";
        Log.Info($"admin client connected from {remote}");
        try
        {
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };

            // Optional auth handshake: if AdminSecret is configured, the FIRST line must be
            // {"cmd":"auth","secret":"..."} with a matching value. Anything else closes the
            // connection without revealing whether auth is required.
            bool authed = string.IsNullOrEmpty(cfg.AdminSecret);
            if (!authed)
            {
                string? first = await reader.ReadLineAsync(stopToken).ConfigureAwait(false);
                if (first == null) return;
                if (!TryAuth(first, cfg.AdminSecret))
                {
                    await writer.WriteLineAsync(JsonSerializer.Serialize(new { ok = false, reason = "auth required" })).ConfigureAwait(false);
                    Log.Warn($"admin client {remote} failed auth");
                    return;
                }
                authed = true;
                await writer.WriteLineAsync(JsonSerializer.Serialize(new { ok = true, authed = true })).ConfigureAwait(false);
            }

            string? line;
            while ((line = await reader.ReadLineAsync(stopToken).ConfigureAwait(false)) != null)
            {
                if (line.Length == 0) continue;
                string response;
                try { response = await DispatchAsync(line).ConfigureAwait(false); }
                catch (Exception ex) { response = JsonSerializer.Serialize(new { ok = false, error = ex.Message }); }
                await writer.WriteLineAsync(response).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
        finally
        {
            try { client.Close(); } catch { }
            Log.Info($"admin client {remote} disconnected");
        }
    }

    /// <summary>Constant-time comparison of the first message's secret against the configured one.</summary>
    private static bool TryAuth(string line, string expectedSecret)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            if (!doc.RootElement.TryGetProperty("cmd", out var cmdEl)) return false;
            if (cmdEl.GetString() != "auth") return false;
            if (!doc.RootElement.TryGetProperty("secret", out var secEl)) return false;
            var supplied = secEl.GetString() ?? "";
            var a = Encoding.UTF8.GetBytes(supplied);
            var b = Encoding.UTF8.GetBytes(expectedSecret);
            return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(a, b);
        }
        catch { return false; }
    }

    private async Task<string> DispatchAsync(string line)
    {
        using var doc = JsonDocument.Parse(line);
        if (!doc.RootElement.TryGetProperty("cmd", out var cmdEl))
            return JsonSerializer.Serialize(new { ok = false, reason = "missing 'cmd'" });
        string cmd = cmdEl.GetString() ?? "";

        switch (cmd)
        {
            case "ping":
                return JsonSerializer.Serialize(new { ok = true, version = "nimbus.proxy/0.1" });

            case "list":
            {
                var sessions = proxy.Sessions.Values
                    .Select(s => new { id = s.Id, phase = s.Phase.ToString(), player = s.PlayerName, uid = s.PlayerUid })
                    .ToArray();
                return JsonSerializer.Serialize(new { sessions });
            }

            case "status":
            {
                long id = doc.RootElement.GetProperty("id").GetInt64();
                if (!proxy.Sessions.TryGetValue(id, out var s))
                    return JsonSerializer.Serialize(new { ok = false, reason = "session not found" });
                return JsonSerializer.Serialize(new
                {
                    ok = true,
                    id = s.Id,
                    phase = s.Phase.ToString(),
                    client = s.ClientRemote,
                    player = s.PlayerName,
                    uid = s.PlayerUid,
                    identCaptured = s.HasIdentification
                });
            }

            case "kick":
            {
                long id = doc.RootElement.GetProperty("id").GetInt64();
                if (!proxy.Sessions.TryGetValue(id, out var s))
                    return JsonSerializer.Serialize(new { ok = false, reason = "session not found" });
                s.Close();
                return JsonSerializer.Serialize(new { ok = true });
            }

            case "servers":
            {
                if (proxy.Registry == null)
                    return JsonSerializer.Serialize(new { ok = false, reason = "Nimbus disabled" });
                bool refresh = doc.RootElement.TryGetProperty("refresh", out var rEl2) && rEl2.GetBoolean();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(proxy.NimbusCfg.RegistryHttpTimeoutSeconds + 1));
                var snap = await proxy.Registry.GetServersAsync(cts.Token, refresh).ConfigureAwait(false);
                if (snap == null)
                    return JsonSerializer.Serialize(new { ok = false, reason = "registry unavailable" });
                return JsonSerializer.Serialize(new { ok = true, snapshot = snap });
            }

            case "swap":
            {
                long id = doc.RootElement.GetProperty("id").GetInt64();
                string serverId = doc.RootElement.TryGetProperty("serverId", out var sidEl) ? (sidEl.GetString() ?? "") : "";
                string? reason = doc.RootElement.TryGetProperty("reason", out var rEl) ? rEl.GetString() : null;
                // mode: "redirect" (default, Velocity-style clean reconnect) or "splice" (legacy in-place TCP swap).
                string mode = doc.RootElement.TryGetProperty("mode", out var mEl) ? (mEl.GetString() ?? "redirect") : "redirect";

                if (!proxy.Sessions.TryGetValue(id, out var session))
                    return JsonSerializer.Serialize(new { ok = false, reason = "session not found" });

                // Resolve target. Prefer registry-by-serverId; fall back to explicit host/port.
                string host;
                int port;
                if (!string.IsNullOrEmpty(serverId) && proxy.Registry != null)
                {
                    using var rcts = new CancellationTokenSource(TimeSpan.FromSeconds(proxy.NimbusCfg.RegistryHttpTimeoutSeconds + 1));
                    var b = await proxy.Registry.ResolveByServerIdAsync(serverId, rcts.Token).ConfigureAwait(false);
                    if (b == null)
                        return JsonSerializer.Serialize(new { ok = false, reason = $"unknown serverId '{serverId}' in registry" });
                    if (b.Stale)
                        return JsonSerializer.Serialize(new { ok = false, reason = $"target '{serverId}' is stale (no recent heartbeat)" });
                    if (b.Maintenance)
                        return JsonSerializer.Serialize(new { ok = false, reason = $"target '{serverId}' is in maintenance" });
                    host = b.PublicHost;
                    port = b.PublicPort;
                }
                else
                {
                    if (!doc.RootElement.TryGetProperty("host", out var hEl) || !doc.RootElement.TryGetProperty("port", out var pEl))
                        return JsonSerializer.Serialize(new { ok = false, reason = "need either serverId (with Nimbus enabled) or host+port" });
                    host = hEl.GetString() ?? "127.0.0.1";
                    port = pEl.GetInt32();
                }

                // Pre-transfer TCP probe (1s) so we fail fast without minting a wasted reservation.
                if (!await TcpProbeAsync(host, port, TimeSpan.FromMilliseconds(1000)).ConfigureAwait(false))
                    return JsonSerializer.Serialize(new { ok = false, reason = $"target {host}:{port} unreachable (tcp probe)" });

                var target = new BackendEndpoint { Host = host, Port = port, ServerId = serverId };
                string? failReason;
                if (string.Equals(mode, "splice", StringComparison.OrdinalIgnoreCase))
                {
                    failReason = await session.RequestSwapAsync(target, proxy.Registry, reason, proxy.NimbusCfg.FailOnRegistryError).ConfigureAwait(false);
                }
                else if (string.Equals(mode, "redirect", StringComparison.OrdinalIgnoreCase))
                {
                    failReason = await session.RequestRedirectAsync(target, proxy.Registry, reason, proxy.NimbusCfg.FailOnRegistryError).ConfigureAwait(false);
                }
                else if (string.Equals(mode, "disconnect", StringComparison.OrdinalIgnoreCase))
                {
                    // Optional stickyTtlSeconds (default 300s = 5 min).
                    TimeSpan? stickyTtl = null;
                    if (doc.RootElement.TryGetProperty("stickyTtlSeconds", out var ttlEl) && ttlEl.ValueKind == JsonValueKind.Number)
                        stickyTtl = TimeSpan.FromSeconds(Math.Clamp(ttlEl.GetInt32(), 10, 3600));
                    failReason = await session.RequestDisconnectAsync(target, proxy.Registry, reason, stickyTtl, proxy.NimbusCfg.FailOnRegistryError).ConfigureAwait(false);
                }
                else
                {
                    return JsonSerializer.Serialize(new { ok = false, reason = $"unknown mode '{mode}' (expected 'redirect', 'splice', or 'disconnect')" });
                }
                return failReason == null
                    ? JsonSerializer.Serialize(new { ok = true, mode, target = new { host, port, serverId } })
                    : JsonSerializer.Serialize(new { ok = false, mode, reason = failReason });
            }

            case "sticky":
            {
                var now = DateTime.UtcNow;
                var entries = proxy.Stickies.Snapshot()
                    .Select(e => new
                    {
                        uid = e.Uid,
                        host = e.Target.Host,
                        port = e.Target.Port,
                        serverId = e.Target.ServerId,
                        ttlSeconds = (int)Math.Max(0, (e.ExpiresAtUtc - now).TotalSeconds),
                        reason = e.Reason,
                    })
                    .ToArray();
                return JsonSerializer.Serialize(new { ok = true, entries });
            }

            default:
                return JsonSerializer.Serialize(new { ok = false, reason = "unknown cmd: " + cmd });
        }
    }

    private static async Task<bool> TcpProbeAsync(string host, int port, TimeSpan timeout)
    {
        using var tcp = new TcpClient { NoDelay = true };
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await tcp.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
            return tcp.Connected;
        }
        catch { return false; }
    }
}
