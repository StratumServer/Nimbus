using System.Net.Sockets;

namespace Nimbus.Proxy;

// Redirect transfer: pre-mint a reservation for the player on the target, forge a vanilla
// Packet_ServerRedirect (Id=29), and close the session. The client reconnects through this
// proxy and a staged sticky route sends it to `target`. Works against any backend that
// supports the vanilla redirect packet, and requires the RedirectFix client mod to avoid the
// vanilla ExitAndSwitchServer crash.
//
// Resets the client's world state. For mid-session swaps without a teardown, see
// RequestSeamlessAsync.
internal sealed partial class ProxySession
{
    public async Task<string?> RequestRedirectAsync(BackendEndpoint target, IRegistryClient? registry = null,
        string? reason = null, bool failOnRegistryError = true)
    {
        ProxyMetrics.RedirectRequested();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        if (closed) { Log.Warn($"[s{Id}] redirect rejected: session is closed"); ProxyMetrics.RedirectFailed(); return "session closed"; }
        if (capturedIdentification == null)
        {
            Log.Warn($"[s{Id}] redirect rejected: no Identification captured yet (phase={Phase})");
            ProxyMetrics.RedirectFailed();
            return $"no Identification captured yet (phase={Phase})";
        }
        if (string.IsNullOrEmpty(target.Host)) { ProxyMetrics.RedirectFailed(); return "redirect target has empty host"; }
        if (target.Port <= 0 || target.Port > 65535) { ProxyMetrics.RedirectFailed(); return $"redirect target has invalid port {target.Port}"; }

        Log.Info($"[s{Id}] REDIRECT -> {target} (phase {Phase}, reason='{reason ?? "<none>"}')");

        var mintFail = await MintReservationIfPossibleAsync(target, registry, reason ?? "proxy redirect", failOnRegistryError).ConfigureAwait(false);
        if (mintFail != null) { ProxyMetrics.RedirectFailed(); return mintFail; }

        // Stage a sticky route on the proxy's PlayerUID->target table. Required because the
        // RedirectFix client mod (Harmony patch on ClientMain.ExitAndSwitchServer) routes the
        // reconnect through the cached connectData (this proxy's address), not the redirect
        // frame's target host. OnClientFrame's sticky lookup picks it up on the next session.
        if (stickies != null && !string.IsNullOrEmpty(capturedPlayerUid))
        {
            var stickyTtl = TimeSpan.FromMinutes(5);
            stickies.Stage(capturedPlayerUid!, target, stickyTtl, reason ?? "proxy redirect");
            Log.Info($"[s{Id}] sticky route staged for uid={capturedPlayerUid} -> {target} (ttl {stickyTtl.TotalSeconds:F0}s)");
        }
        else
        {
            Log.Warn($"[s{Id}] no sticky staged (stickies={(stickies != null ? "set" : "null")}, uid='{capturedPlayerUid ?? ""}'), reconnect may land on default backend");
        }

        // Build the redirect host string per vanilla VS convention: "host" or "host:port".
        string hostString = (target.Port > 0 && target.Port != 42420)
            ? $"{target.Host}:{target.Port}"
            : target.Host;
        string displayName = string.IsNullOrEmpty(target.ServerId) ? hostString : target.ServerId;

        byte[] frame;
        try { frame = RedirectBuilder.BuildRedirectFrame(hostString, displayName); }
        catch (Exception ex) { Log.Warn($"[s{Id}] redirect frame build failed: {ex.Message}"); ProxyMetrics.RedirectFailed(); return $"frame build failed: {ex.Message}"; }

        try
        {
            await clientStream.WriteAsync(frame, sessionStopToken).ConfigureAwait(false);
            await clientStream.FlushAsync(sessionStopToken).ConfigureAwait(false);
            Log.Info($"[s{Id}] redirect frame sent to client ({frame.Length}B) host='{hostString}' name='{displayName}'");
        }
        catch (Exception ex)
        {
            Log.Warn($"[s{Id}] redirect write to client failed: {ex.Message}");
            ProxyMetrics.RedirectFailed();
            return $"client write failed: {ex.Message}";
        }

        // 250ms is enough for the client to process the in-flight packet and start its own
        // disconnect before we tear the sockets down.
        try { await Task.Delay(250, sessionStopToken).ConfigureAwait(false); } catch { }
        Close();
        ProxyMetrics.RedirectSucceeded();
        Log.Info($"[s{Id}] AUDIT op=redirect target={target} reason='{reason ?? ""}' uid={capturedPlayerUid ?? ""} result=ok duration_ms={sw.ElapsedMilliseconds}");
        return null;
    }

    // Shared by Redirect and Seamless: pre-mint a Nimbus reservation so the target backend
    // accepts the player by UID without re-running auth. Returns null on success or a short
    // failure reason. When `failOnRegistryError` is false, mint failures are logged and null
    // is returned (caller proceeds and the backend auth gate decides).
    private async Task<string?> MintReservationIfPossibleAsync(BackendEndpoint target, IRegistryClient? registry, string reason, bool failOnRegistryError)
    {
        if (registry == null || string.IsNullOrEmpty(target.ServerId))
        {
            if (registry != null) Log.Trace($"[s{Id}] mint skipped: target has no ServerId");
            return null;
        }

        if (string.IsNullOrEmpty(capturedPlayerUid))
        {
            if (failOnRegistryError)
            {
                Log.Warn($"[s{Id}] mint aborted: no PlayerUID parsed from Identification");
                return "no PlayerUID parsed from Identification";
            }
            Log.Warn($"[s{Id}] proceeding without reservation: no PlayerUID parsed");
            return null;
        }

        using var mintCts = CancellationTokenSource.CreateLinkedTokenSource(sessionStopToken);
        mintCts.CancelAfter(TimeSpan.FromSeconds(10));
        var reservation = await registry.MintReservationAsync(
            capturedPlayerUid!, capturedPlayerName ?? "", target.ServerId, reason, mintCts.Token,
            ClientEndpoint.ip, ClientEndpoint.port).ConfigureAwait(false);
        if (reservation == null)
        {
            if (failOnRegistryError)
            {
                Log.Warn($"[s{Id}] mint aborted: registry mint failed for uid={capturedPlayerUid} target={target.ServerId}");
                return "registry mint failed";
            }
            Log.Warn($"[s{Id}] proceeding without reservation: registry mint failed");
            return null;
        }

        Log.Info($"[s{Id}] reservation minted id={reservation.Id} target={reservation.TargetServerId} ttl={reservation.ExpiresAtUnix - DateTimeOffset.UtcNow.ToUnixTimeSeconds()}s");
        return null;
    }
}
