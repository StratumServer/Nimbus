using System.Net.Sockets;

namespace Nimbus.Proxy;

// Live transfer path. The proxy dials the next backend, replays Identification, then swaps the
// upstream socket while the client keeps its TCP connection open.
//
// This needs the Nimbus client mod for normal use. Unmodded clients should stay on redirect.
internal sealed partial class ProxySession
{
    public async Task<string?> RequestSeamlessAsync(BackendEndpoint target, IRegistryClient? registry = null,
        string? swapReason = null, bool failOnRegistryError = true, string? clientTransferId = null)
    {
        ProxyMetrics.SeamlessRequested();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        if (closed) { Log.Warn($"[s{Id}] seamless rejected: session is closed"); ProxyMetrics.SeamlessFailed(); return "session closed"; }
        if (capturedIdentification == null)
        {
            Log.Warn($"[s{Id}] seamless rejected: no Identification captured yet (phase={Phase})");
            ProxyMetrics.SeamlessFailed();
            return $"no Identification captured yet (phase={Phase})";
        }

        TcpClient? oldUpstream;
        CancellationTokenSource? oldCts;
        Task? oldPumpC2S;
        Task? oldPumpS2C;
        lock (swapLock)
        {
            if (swapping) { Log.Warn($"[s{Id}] seamless already in progress"); ProxyMetrics.SeamlessFailed(); return "seamless already in progress"; }
            swapping = true;
            oldUpstream = upstream;
            oldCts = pumpCts;
            oldPumpC2S = pumpC2S;
            oldPumpS2C = pumpS2C;
        }

        // ServerPreConnect: handlers can swap target or cancel before we open the new upstream.
        if (events != null)
        {
            var pre = new ServerPreConnectEvent(this, target.ToServerInfo(), swapReason);
            await events.FireAsync(pre).ConfigureAwait(false);
            if (pre.IsCancelled)
            {
                Log.Warn($"[s{Id}] seamless cancelled by handler: {pre.CancelReason}");
                swapping = false;
                ProxyMetrics.SeamlessFailed();
                return $"cancelled: {pre.CancelReason}";
            }
            target = pre.Target.ToEndpoint();
        }

        // Checked after ServerPreConnect, because a handler is allowed to swap the target and it
        // is the final destination that has to be clear of bans.
        var banFail = CheckTargetBan(target);
        if (banFail != null)
        {
            Log.Warn($"[s{Id}] seamless rejected: {banFail}");
            swapping = false;
            ProxyMetrics.SeamlessFailed();
            return banFail;
        }

        var whitelistFail = CheckTargetWhitelist(target);
        if (whitelistFail != null)
        {
            Log.Warn($"[s{Id}] seamless rejected: {whitelistFail}");
            swapping = false;
            ProxyMetrics.SeamlessFailed();
            return whitelistFail;
        }

        // The splice below writes the captured Identification bytes at `target`. That is only
        // ever safe when `target` is the backend that already has them.
        var replayFail = CheckIdentificationReplay(target, operatorOptedIn: cfg.Transfers.EnableUnsafeSeamlessSplice);
        if (replayFail != null)
        {
            Log.Warn($"[s{Id}] seamless rejected: {replayFail}");
            swapping = false;
            ProxyMetrics.SeamlessFailed();
            return replayFail;
        }

        var mintFail = await MintReservationIfPossibleAsync(target, registry, swapReason ?? "proxy seamless", failOnRegistryError, clientTransferId).ConfigureAwait(false);
        if (mintFail != null) { swapping = false; ProxyMetrics.SeamlessFailed(); return mintFail; }

        var newUp = new TcpClient { NoDelay = true };
        using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(sessionStopToken))
        {
            connectCts.CancelAfter(cfg.Advanced.ConnectTimeoutMs);
            try { await newUp.ConnectAsync(target.Host, target.Port, connectCts.Token).ConfigureAwait(false); }
            catch (Exception ex)
            {
                Log.Warn($"[s{Id}] seamless failed: could not reach {target}: {ex.Message}");
                // The swap is abandoned and the player stays on the current upstream, which was
                // never touched. All that is left is dropping the socket that failed to connect.
                try { newUp.Close(); } catch { /* never connected, nothing to report */ }
                swapping = false;
                ProxyMetrics.SeamlessFailed();
                return $"connect failed: {ex.Message}";
            }
        }

        if (!await TryWriteProxyProtocolAsync(newUp, target).ConfigureAwait(false))
        {
            try { newUp.Close(); } catch { /* the header write already failed; the socket is done */ }
            swapping = false;
            ProxyMetrics.SeamlessFailed();
            return "proxy protocol header write failed";
        }

        try
        {
            await newUp.GetStream().WriteAsync(capturedIdentification, sessionStopToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn($"[s{Id}] seamless failed: write Identification: {ex.Message}");
            try { newUp.Close(); } catch { /* the Identification write already failed; the socket is done */ }
            swapping = false;
            ProxyMetrics.SeamlessFailed();
            return $"write Identification failed: {ex.Message}";
        }

        // Wait for the old pumps to stop before the new backend writes to the client stream.
        // Past this point the swap is committed, so the old side is torn down whatever it says.
        try { if (oldCts != null) await oldCts.CancelAsync().ConfigureAwait(false); }
        catch { /* the old pumps already stopped on their own */ }
        try { oldUpstream?.Close(); } catch { /* the old backend already dropped the socket */ }
        try
        {
            var waitC2S = oldPumpC2S != null ? SafeAwait(oldPumpC2S) : Task.CompletedTask;
            var waitS2C = oldPumpS2C != null ? SafeAwait(oldPumpS2C) : Task.CompletedTask;
            using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(sessionStopToken);
            waitCts.CancelAfter(TimeSpan.FromSeconds(5));
            var waitAll = Task.WhenAll(waitC2S, waitS2C);
            var completed = await Task.WhenAny(waitAll, Task.Delay(Timeout.Infinite, waitCts.Token)).ConfigureAwait(false);
            if (completed != waitAll)
                Log.Warn($"[s{Id}] seamless: old pumps did not exit within 5s; proceeding anyway (may cause stream corruption)");
        }
        catch { /* the wait itself failing is the same outcome as the timeout, already warned above */ }

        var previous = oldUpstream != null && currentBackend != null ? currentBackend.ToServerInfo() : null;
        upstream = newUp;
        currentBackend = target;
        UpdateUdpOverride(target);
        StartPumps();
        // RunAsync starts waiting on the new pumps after this flips back.
        swapping = false;

        ProxyMetrics.SeamlessSucceeded();
        var prevId = previous?.ServerId ?? previous?.ToString() ?? "?";
        Log.Info($"[s{Id}] {capturedPlayerName ?? "?"}: {prevId} → {target.ServerId ?? target.ToString()} (seamless, {sw.ElapsedMilliseconds}ms)");
        if (events != null)
        {
            // The swap is done and the player is on the new backend. Both of these announce it;
            // neither can refuse it, and one throwing must not cost the other its notification.
            try { await events.FireAsync(new ServerPostConnectEvent(this, target.ToServerInfo(), previous)).ConfigureAwait(false); }
            catch { /* the player is on the new backend regardless */ }
            try { await events.FireAsync(new PlayerTransferredEvent(this, previous, target.ToServerInfo(), "seamless")).ConfigureAwait(false); }
            catch { /* same: the transfer already happened */ }
        }
        return null;
    }
}
