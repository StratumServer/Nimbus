using System.Net.Sockets;

namespace Nimbus.Proxy;

// Live transfer path. The proxy dials the next backend, replays Identification, then swaps the
// upstream socket while the client keeps its TCP connection open.
//
// This needs the Nimbus client mod for normal use. Unmodded clients should stay on redirect.
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
        }
        catch (Exception ex)
        {
            Log.Warn($"[s{Id}] seamless failed: write Identification: {ex.Message}");
            try { newUp.Close(); } catch { }
            swapping = false;
            ProxyMetrics.SeamlessFailed();
            return $"write Identification failed: {ex.Message}";
        }

        // Wait for the old pumps to stop before the new backend writes to the client stream.
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
            try { await events.FireAsync(new ServerPostConnectEvent(this, target.ToServerInfo(), previous)).ConfigureAwait(false); }
            catch { }
            try { await events.FireAsync(new PlayerTransferredEvent(this, previous, target.ToServerInfo(), "seamless")).ConfigureAwait(false); }
            catch { }
        }
        return null;
    }
}
