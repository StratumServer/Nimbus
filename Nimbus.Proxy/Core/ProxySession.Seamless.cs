using System.Net.Sockets;

namespace Nimbus.Proxy;

// Seamless transfer: dial a new upstream, replay the captured Identification, then atomically
// swap the upstream socket out from under the live client. The client TCP never sees a FIN.
//
// Best-effort. In-flight client bytes between read+write may be dropped during the swap. The
// intended use is right after the Identification exchange and before heavy gameplay traffic.
//
// In vanilla VS this is unsafe for full mid-session world handoff because the client retains
// its old world/atlas state. The Nimbus client+server mod will gate world teardown so this
// becomes the no-flicker transfer path. Until then it remains config-gated behind
// `transfers.allow_seamless`.
internal sealed partial class ProxySession
{
    public async Task<string?> RequestSeamlessAsync(BackendEndpoint target, IRegistryClient? registry = null,
        string? swapReason = null, bool failOnRegistryError = true)
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

        Log.Info($"[s{Id}] SEAMLESS -> {target} (phase {Phase}, captured ident {capturedIdentification.Length}B, reason='{swapReason ?? "<none>"}')");

        // ServerPreConnect: handlers can swap target or cancel before we open the new upstream.
        if (events != null)
        {
            var pre = new ServerPreConnectEvent(this, ServerInfo.From(target), swapReason);
            await events.FireAsync(pre).ConfigureAwait(false);
            if (pre.IsCancelled)
            {
                Log.Warn($"[s{Id}] seamless cancelled by handler: {pre.CancelReason}");
                swapping = false;
                ProxyMetrics.SeamlessFailed();
                return $"cancelled: {pre.CancelReason}";
            }
            if (pre.Target is ServerInfo si) target = si.ToEndpoint();
        }

        var mintFail = await MintReservationIfPossibleAsync(target, registry, swapReason ?? "proxy seamless", failOnRegistryError).ConfigureAwait(false);
        if (mintFail != null) { swapping = false; ProxyMetrics.SeamlessFailed(); return mintFail; }

        var newUp = new TcpClient { NoDelay = true };
        using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(sessionStopToken))
        {
            connectCts.CancelAfter(cfg.Advanced.ConnectTimeoutMs);
            try { await newUp.ConnectAsync(target.Host, target.Port, connectCts.Token).ConfigureAwait(false); }
            catch (Exception ex)
            {
                Log.Warn($"[s{Id}] seamless failed: could not reach {target}: {ex.Message}");
                try { newUp.Close(); } catch { }
                swapping = false;
                ProxyMetrics.SeamlessFailed();
                return $"connect failed: {ex.Message}";
            }
        }

        if (!await TryWriteProxyProtocolAsync(newUp, target).ConfigureAwait(false))
        {
            try { newUp.Close(); } catch { }
            swapping = false;
            ProxyMetrics.SeamlessFailed();
            return "proxy protocol header write failed";
        }

        try
        {
            await newUp.GetStream().WriteAsync(capturedIdentification, sessionStopToken).ConfigureAwait(false);
            Log.Info($"[s{Id}] replayed Identification to new backend");
        }
        catch (Exception ex)
        {
            Log.Warn($"[s{Id}] seamless failed: write Identification: {ex.Message}");
            try { newUp.Close(); } catch { }
            swapping = false;
            ProxyMetrics.SeamlessFailed();
            return $"write Identification failed: {ex.Message}";
        }

        // Atomic swap: cancel old pumps first, wait for them to fully exit, then close the old
        // upstream and start fresh pumps. Without this wait, the old s->c pump can interleave
        // in-flight bytes with the new pump's writes, corrupting the client frame stream.
        try { oldCts?.Cancel(); } catch { }
        try { oldUpstream?.Close(); } catch { }
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
        catch { }

        var previous = oldUpstream != null && currentBackend != null ? ServerInfo.From(currentBackend) : null;
        upstream = newUp;
        currentBackend = target;
        UpdateUdpOverride(target);
        StartPumps();
        // Release the flag AFTER new pumps are installed so RunAsync's spin-wait can re-await them.
        swapping = false;

        Log.Info($"[s{Id}] seamless complete; new upstream {target} is live");
        ProxyMetrics.SeamlessSucceeded();
        Log.Info($"[s{Id}] AUDIT op=seamless target={target} reason='{swapReason ?? ""}' uid={capturedPlayerUid ?? ""} result=ok duration_ms={sw.ElapsedMilliseconds}");
        if (events != null)
        {
            try { await events.FireAsync(new ServerPostConnectEvent(this, ServerInfo.From(target), previous)).ConfigureAwait(false); }
            catch { }
        }
        return null;
    }
}
