using System.Net.Sockets;

namespace Nimbus.Proxy;

// Redirect transfer: pre-mint a reservation for the player on the target, forge a vanilla
// Packet_ServerRedirect (Id=29), and close the session. The client reconnects through this
// proxy and a staged sticky route sends it to `target`. Works against any backend that
// supports the vanilla redirect packet, and requires the RedirectFix client mod to avoid the
// vanilla ExitAndSwitchServer crash.
internal sealed partial class ProxySession
{
    // `stickyAttempt` is how many redirects this staged route will have consumed once this one
    // fires. A transfer asked for by an operator or a plugin starts over at 1; the late sticky
    // fallback in ProxySession passes the running count so a route that keeps missing gives up
    // instead of bouncing the player between backends.
    public async Task<string?> RequestRedirectAsync(BackendEndpoint target, IRegistryClient? registry = null,
        string? reason = null, bool failOnRegistryError = true, string? clientTransferId = null,
        int stickyAttempt = 1)
    {
        ProxyMetrics.RedirectRequested();
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var preFail = CheckRedirectPreconditions(target);
        if (preFail != null) { ProxyMetrics.RedirectFailed(); return preFail; }

        var mintFail = await MintReservationIfPossibleAsync(target, registry, reason ?? "proxy redirect", failOnRegistryError, clientTransferId).ConfigureAwait(false);
        if (mintFail != null) { ProxyMetrics.RedirectFailed(); return mintFail; }

        StageRedirectSticky(target, reason, stickyAttempt);

        var sendFail = await SendRedirectFrameAsync(target).ConfigureAwait(false);
        if (sendFail != null) { ProxyMetrics.RedirectFailed(); return sendFail; }

        // 250ms is enough for the client to process the in-flight packet and start its own
        // disconnect before we tear the sockets down.
        try { await Task.Delay(250, sessionStopToken).ConfigureAwait(false); } catch { /* proxy stopping: the client gets its packet or it does not */ }
        Close();
        ProxyMetrics.RedirectSucceeded();
        var fromId = currentBackend != null ? (currentBackend.ServerId ?? currentBackend.ToString()) : "?";
        Log.Info($"[s{Id}] {capturedPlayerName ?? "?"}: {fromId} → {target.ServerId ?? target.ToString()} (redirect, {sw.ElapsedMilliseconds}ms)");
        if (events != null)
        {
            var from = currentBackend?.ToServerInfo();
            // The redirect packet is already on the wire and the session is closed. This is a
            // notification about something that happened, not a step that can still be undone.
            try { await events.FireAsync(new PlayerTransferredEvent(this, from, target.ToServerInfo(), "redirect")).ConfigureAwait(false); }
            catch { /* the transfer stands whatever the handler thinks of it */ }
        }
        return null;
    }

    // Everything that can refuse a redirect before any of it becomes visible to the player, in
    // the order it has always been asked. Returns the reason to hand back, or null to go ahead.
    private string? CheckRedirectPreconditions(BackendEndpoint target)
    {
        if (closed) { Log.Warn($"[s{Id}] redirect rejected: session is closed"); return "session closed"; }
        if (capturedIdentification == null)
        {
            Log.Warn($"[s{Id}] redirect rejected: no Identification captured yet (phase={Phase})");
            return $"no Identification captured yet (phase={Phase})";
        }
        // The two endpoint shape checks answer without a log line, unlike every other refusal
        // here: a target with no host or no port is a caller passing nonsense rather than a
        // player being turned away, and the returned reason goes straight back to that caller.
        if (string.IsNullOrEmpty(target.Host)) return "redirect target has empty host";
        if (target.Port <= 0 || target.Port > 65535) return $"redirect target has invalid port {target.Port}";

        return CheckTransferGates(target, "redirect");
    }

    // RedirectFix reconnects through the cached proxy address. The next session consumes this
    // route before choosing a backend. Stage it under the client address as well as the UID:
    // this is the only point where both are known, and the reconnect will arrive with a
    // LoginTokenQuery that carries no identity to match the UID against (#57).
    private void StageRedirectSticky(BackendEndpoint target, string? reason, int stickyAttempt)
    {
        if (stickies != null && !string.IsNullOrEmpty(capturedPlayerUid))
        {
            stickies.Stage(capturedPlayerUid, ClientEndpoint.ip, target, StickyRouteTable.UidTtl,
                reason ?? "proxy redirect", stickyAttempt);
        }
        else
        {
            Log.Warn($"[s{Id}] no sticky staged (stickies={(stickies != null ? "set" : "null")}, uid='{capturedPlayerUid ?? ""}'), reconnect may land on default backend");
        }
    }

    // Forge the vanilla Packet_ServerRedirect and put it on the client's wire. Returns the failure
    // reason, or null once the bytes are flushed. Nothing here is recoverable: a client that did
    // not get the packet is not going to move, and the caller reports that as the redirect failing.
    private async Task<string?> SendRedirectFrameAsync(BackendEndpoint target)
    {
        // What the forged packet carries: the configured proxy address when set, else the
        // backend's address per vanilla VS convention ("host" or "host:port"). See
        // RedirectTargeting for why stamping the proxy matters once vanilla clients follow
        // the stamped host literally (#18).
        var stamp = RedirectTargeting.Resolve(cfg.Transfers, target);
        string hostString = stamp.HostString;
        string displayName = string.IsNullOrEmpty(target.ServerId) ? hostString : target.ServerId;
        Log.Trace($"[s{Id}] redirect stamps {(stamp.ProxyStamped ? "proxy address" : "backend address")} '{hostString}'");

        byte[] frame;
        try { frame = RedirectBuilder.BuildRedirectFrame(hostString, displayName); }
        catch (Exception ex) { Log.Warn($"[s{Id}] redirect frame build failed: {ex.Message}"); return $"frame build failed: {ex.Message}"; }

        try
        {
            await clientStream.WriteAsync(frame, sessionStopToken).ConfigureAwait(false);
            await clientStream.FlushAsync(sessionStopToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn($"[s{Id}] redirect write to client failed: {ex.Message}");
            return $"client write failed: {ex.Message}";
        }

        return null;
    }

    // Shared by Redirect and Seamless: pre-mint a Nimbus reservation so the target backend
    // accepts the player by UID without re-running auth. Returns null on success or a short
    // failure reason. When `failOnRegistryError` is false, mint failures are logged and null
    // is returned (caller proceeds and the backend auth gate decides).
    private async Task<string?> MintReservationIfPossibleAsync(BackendEndpoint target, IRegistryClient? registry, string reason, bool failOnRegistryError,
        string? clientTransferId = null)
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
            capturedPlayerUid, capturedPlayerName ?? "", target.ServerId, reason, mintCts.Token,
            ClientEndpoint.ip, ClientEndpoint.port, clientTransferId).ConfigureAwait(false);
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

        return null;
    }
}
