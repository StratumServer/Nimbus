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

        // Asked before the claim below, so these two refusals must not give a claim back.
        var preFail = CheckSeamlessPreflight();
        if (preFail != null) { ProxyMetrics.SeamlessFailed(); return preFail; }

        var claimed = TryClaimSwap();
        if (claimed == null)
        {
            // Deliberately not AbortSwap: the claim belongs to the swap already running, and
            // handing it back here would let two swaps install upstreams over each other.
            Log.Warn($"[s{Id}] seamless already in progress");
            ProxyMetrics.SeamlessFailed();
            return "seamless already in progress";
        }
        var predecessor = claimed.Value;

        var (settled, targetFail) = await ResolveSwapTargetAsync(target, swapReason).ConfigureAwait(false);
        if (targetFail != null) return AbortSwap(targetFail);
        target = settled;

        var mintFail = await MintReservationIfPossibleAsync(target, registry, swapReason ?? "proxy seamless", failOnRegistryError, clientTransferId).ConfigureAwait(false);
        if (mintFail != null) return AbortSwap(mintFail);

        var (newUp, openFail) = await OpenSwapUpstreamAsync(target).ConfigureAwait(false);
        if (openFail != null) return AbortSwap(openFail);

        await RetireSwapPredecessorAsync(predecessor).ConfigureAwait(false);

        var previous = predecessor.Upstream != null && currentBackend != null ? currentBackend.ToServerInfo() : null;
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

    // What a committed swap has to retire: the sockets, the token source and the pumps this
    // session was running when it claimed the swap. Captured under swapLock in one go, because
    // reading them one at a time afterwards can catch a half-installed set.
    private readonly record struct SwapPredecessor(
        TcpClient? Upstream, CancellationTokenSource? Cts, Task? PumpC2S, Task? PumpS2C);

    // Refusals that apply before this session has claimed the swap, so none of them has anything
    // to undo. Returns the reason, or null to carry on to the claim.
    private string? CheckSeamlessPreflight()
    {
        if (closed) { Log.Warn($"[s{Id}] seamless rejected: session is closed"); return "session closed"; }
        if (capturedIdentification == null)
        {
            Log.Warn($"[s{Id}] seamless rejected: no Identification captured yet (phase={Phase})");
            return $"no Identification captured yet (phase={Phase})";
        }
        return null;
    }

    // Take the swap for this session, or null when another one already holds it. The lock covers
    // the test and the set together so two callers cannot both come away thinking they won.
    private SwapPredecessor? TryClaimSwap()
    {
        lock (swapLock)
        {
            if (swapping) return null;
            swapping = true;
            return new SwapPredecessor(upstream, pumpCts, pumpC2S, pumpS2C);
        }
    }

    // Give the claim back and report the refusal. Every path that fails after TryClaimSwap has
    // won has to come through here: a swap left claimed can never be retried on this session.
    private string AbortSwap(string reason)
    {
        swapping = false;
        ProxyMetrics.SeamlessFailed();
        return reason;
    }

    // Where this swap is actually going, and whether the player may go there. The gates run after
    // ServerPreConnect on purpose: a handler is allowed to swap the target, and it is the final
    // destination that has to be clear of bans. Returns the settled target and a failure reason.
    private async Task<(BackendEndpoint target, string? fail)> ResolveSwapTargetAsync(BackendEndpoint target, string? swapReason)
    {
        var (settled, cancelled, cancelReason) = await FirePreConnectAsync(target, swapReason, label: "seamless").ConfigureAwait(false);
        if (cancelled) return (settled, $"cancelled: {cancelReason}");
        target = settled;

        var gateFail = CheckTransferGates(target, "seamless");
        if (gateFail != null) return (target, gateFail);

        // The splice writes the captured Identification bytes at `target`. That is only ever safe
        // when `target` is the backend that already has them.
        var replayFail = CheckIdentificationReplay(target, operatorOptedIn: cfg.Transfers.EnableUnsafeSeamlessSplice);
        if (replayFail != null)
        {
            Log.Warn($"[s{Id}] seamless rejected: {replayFail}");
            return (target, replayFail);
        }

        return (target, null);
    }

    // Dial the next backend and hand it the captured Identification, so it is ready to be spliced
    // in. Nothing here touches the live upstream: until this returns a socket, the player is still
    // being pumped to the backend they are on, and every failure just drops the socket it opened.
    private async Task<(TcpClient? up, string? fail)> OpenSwapUpstreamAsync(BackendEndpoint target)
    {
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
                return (null, $"connect failed: {ex.Message}");
            }
        }

        if (!await TryWriteProxyProtocolAsync(newUp, target).ConfigureAwait(false))
        {
            try { newUp.Close(); } catch { /* the header write already failed; the socket is done */ }
            return (null, "proxy protocol header write failed");
        }

        try
        {
            await newUp.GetStream().WriteAsync(capturedIdentification, sessionStopToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn($"[s{Id}] seamless failed: write Identification: {ex.Message}");
            try { newUp.Close(); } catch { /* the Identification write already failed; the socket is done */ }
            return (null, $"write Identification failed: {ex.Message}");
        }

        return (newUp, null);
    }

    // Stop the old pumps before the new backend writes to the client stream. Past this point the
    // swap is committed, so the old side is torn down whatever it says and none of these steps
    // can refuse: the worst case is the 5s cap expiring, which is warned about and carried on from.
    private async Task RetireSwapPredecessorAsync(SwapPredecessor predecessor)
    {
        try { if (predecessor.Cts != null) await predecessor.Cts.CancelAsync().ConfigureAwait(false); }
        catch { /* the old pumps already stopped on their own */ }
        try { predecessor.Upstream?.Close(); } catch { /* the old backend already dropped the socket */ }
        try
        {
            var waitC2S = predecessor.PumpC2S != null ? SafeAwait(predecessor.PumpC2S) : Task.CompletedTask;
            var waitS2C = predecessor.PumpS2C != null ? SafeAwait(predecessor.PumpS2C) : Task.CompletedTask;
            using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(sessionStopToken);
            waitCts.CancelAfter(TimeSpan.FromSeconds(5));
            var waitAll = Task.WhenAll(waitC2S, waitS2C);
            var completed = await Task.WhenAny(waitAll, Task.Delay(Timeout.Infinite, waitCts.Token)).ConfigureAwait(false);
            if (completed != waitAll)
                Log.Warn($"[s{Id}] seamless: old pumps did not exit within 5s; proceeding anyway (may cause stream corruption)");
        }
        catch { /* the wait itself failing is the same outcome as the timeout, already warned above */ }
    }
}
