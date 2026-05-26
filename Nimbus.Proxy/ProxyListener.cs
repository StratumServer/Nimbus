using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace Nimbus.Proxy;

// Accepts client TCP connections, hands each to a ProxySession, and tracks live sessions so
// the admin endpoint can look them up by id.
internal sealed class ProxyListener
{
    private readonly ProxyConfig cfg;
    private readonly CancellationToken stopToken;
    private long sessionCounter;
    public ConcurrentDictionary<long, ProxySession> Sessions { get; } = new();

    // Non-null when Nimbus.Enabled is true.
    public RegistryClient? Registry { get; }

    public BackendRouter Router { get; }

    // "Next reconnect for this uid goes here" routes. Populated by the disconnect-transfer path
    // and consumed by ProxySession on Identification capture.
    public StickyRouteTable Stickies { get; } = new();

    // Per-client-IP UDP routing overrides. Set when a TCP swap retargets a session, so the UDP
    // relay also retargets. Cleared on session close.
    public UdpRouteOverrides UdpOverrides { get; } = new();

    public NimbusConfig NimbusCfg => cfg.Nimbus;

    public ProxyListener(ProxyConfig cfg, CancellationToken stopToken, RegistryClient? registry = null)
    {
        this.cfg = cfg;
        this.stopToken = stopToken;
        Registry = registry;
        Router = new BackendRouter(cfg, registry);
    }

    public async Task RunAsync()
    {
        var bindAddr = IPAddress.Parse(cfg.ListenHost == "0.0.0.0" ? "0.0.0.0" : cfg.ListenHost);
        var listener = new TcpListener(bindAddr, cfg.ListenPort);
        listener.Start();
        if (cfg.Backends.Count > 0)
            Log.Info($"listening on {bindAddr}:{cfg.ListenPort} -> pool of {cfg.Backends.Count} backend(s)");
        else
            Log.Info($"listening on {bindAddr}:{cfg.ListenPort} -> backend {cfg.DefaultBackend}");

        // Periodic sweep of expired sticky routes. Without this, staged routes for players who
        // never reconnect would accumulate.
        var sweepTask = Task.Run(async () =>
        {
            while (!stopToken.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(60), stopToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                int dropped = Stickies.SweepExpired();
                if (dropped > 0) Log.Trace($"sticky sweep: dropped {dropped} expired entries");
            }
        }, stopToken);

        // Transfer-intent dispatcher: poll the registry queue and run the existing swap path
        // for any intent whose PlayerUid matches a live session on this proxy. Intents whose
        // player isn't on this proxy are dropped (best-effort: a multi-proxy deployment will
        // see the intent on whichever proxy drains first, which won't always be the right one).
        var dispatcherTask = Registry == null ? Task.CompletedTask : Task.Run(async () =>
        {
            var period = TimeSpan.FromMilliseconds(Math.Max(250, NimbusCfg.TransferIntentPollMs));
            while (!stopToken.IsCancellationRequested)
            {
                try { await Task.Delay(period, stopToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                try
                {
                    var intents = await Registry.DrainTransferIntentsAsync(stopToken).ConfigureAwait(false);
                    if (intents.Count == 0) continue;
                    foreach (var it in intents) _ = Task.Run(() => DispatchIntentAsync(it), stopToken);
                }
                catch (Exception ex) { Log.Warn($"transfer-intent poll failed: {ex.GetType().Name}: {ex.Message}"); }
            }
        }, stopToken);

        try
        {
            while (!stopToken.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(stopToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }

                long id = Interlocked.Increment(ref sessionCounter);
                var session = new ProxySession(id, cfg, client, stopToken, Stickies, Registry, UdpOverrides);
                Sessions[id] = session;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        BackendEndpoint? target;
                        string? selectReason;
                        using (var selectCts = CancellationTokenSource.CreateLinkedTokenSource(stopToken))
                        {
                            selectCts.CancelAfter(TimeSpan.FromSeconds(5));
                            (target, selectReason) = await Router.SelectAsync(selectCts.Token).ConfigureAwait(false);
                        }
                        if (target == null)
                        {
                            Log.Warn($"[s{id}] no healthy backend: {selectReason}; sending forged disconnect");
                            await TryForgeDisconnectAsync(client, $"No backend available right now ({selectReason}). Please try again shortly.").ConfigureAwait(false);
                            try { client.Close(); } catch { }
                            return;
                        }
                        await session.RunAsync(target).ConfigureAwait(false);
                    }
                    catch (Exception ex) { Log.Warn($"[s{id}] session crashed: {ex.GetType().Name}: {ex.Message}"); }
                    finally { Sessions.TryRemove(id, out _); }
                }, stopToken);
            }
        }
        finally
        {
            listener.Stop();
            Log.Info("listener stopped");
        }
    }

    // Best-effort: write a forged Packet_ServerDisconnect to a client we never wired upstream.
    // Swallow errors; the only purpose is to give the player a useful message before close.
    private static async Task TryForgeDisconnectAsync(TcpClient client, string message)
    {
        try
        {
            var frame = DisconnectBuilder.BuildDisconnectFrame(message);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await client.GetStream().WriteAsync(frame, cts.Token).ConfigureAwait(false);
            await client.GetStream().FlushAsync(cts.Token).ConfigureAwait(false);
            try { await Task.Delay(150, cts.Token).ConfigureAwait(false); } catch { }
        }
        catch { }
    }

    // Execute one drained transfer intent. Looks up a live session for the intent's PlayerUid;
    // if found, resolves the target and runs the same swap path as `nimctl swap`. No-op when
    // no matching session is on this proxy.
    private async Task DispatchIntentAsync(Nimbus.Shared.Models.TransferIntent intent)
    {
        if (Registry == null) return;
        if (string.IsNullOrEmpty(intent.PlayerUid) || string.IsNullOrEmpty(intent.TargetServerId)) return;

        ProxySession? match = null;
        foreach (var s in Sessions.Values)
        {
            if (string.Equals(s.PlayerUid, intent.PlayerUid, StringComparison.OrdinalIgnoreCase)) { match = s; break; }
        }
        if (match == null)
        {
            Log.Trace($"intent {intent.Id} for uid={intent.PlayerUid} -> no live session on this proxy, dropping");
            return;
        }

        try
        {
            using var rcts = new CancellationTokenSource(TimeSpan.FromSeconds(NimbusCfg.RegistryHttpTimeoutSeconds + 1));
            var b = await Registry.ResolveByServerIdAsync(intent.TargetServerId, rcts.Token).ConfigureAwait(false);
            if (b == null) { Log.Warn($"intent {intent.Id}: unknown serverId '{intent.TargetServerId}'"); return; }
            if (b.Stale) { Log.Warn($"intent {intent.Id}: target '{intent.TargetServerId}' is stale"); return; }
            if (b.Maintenance) { Log.Warn($"intent {intent.Id}: target '{intent.TargetServerId}' is in maintenance"); return; }

            var target = new BackendEndpoint { Host = b.PublicHost, Port = b.PublicPort, ServerId = intent.TargetServerId };
            string? failReason;
            if (string.Equals(intent.Mode, "splice", StringComparison.OrdinalIgnoreCase))
                failReason = await match.RequestSwapAsync(target, Registry, intent.Reason, NimbusCfg.FailOnRegistryError).ConfigureAwait(false);
            else if (string.Equals(intent.Mode, "disconnect", StringComparison.OrdinalIgnoreCase))
                failReason = await match.RequestDisconnectAsync(target, Registry, intent.Reason, null, NimbusCfg.FailOnRegistryError).ConfigureAwait(false);
            else
                failReason = await match.RequestRedirectAsync(target, Registry, intent.Reason, NimbusCfg.FailOnRegistryError).ConfigureAwait(false);

            if (failReason != null)
                Log.Warn($"intent {intent.Id} dispatch failed: {failReason}");
            else
                Log.Info($"intent {intent.Id} dispatched: {intent.PlayerName}({intent.PlayerUid}) -> {intent.TargetServerId} via {intent.Mode}");
        }
        catch (Exception ex)
        {
            Log.Warn($"intent {intent.Id} dispatch error: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
