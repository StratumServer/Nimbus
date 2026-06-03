using System.Net.Sockets;

namespace Nimbus.Proxy;

// A single proxied player session. Owns the client socket and the current upstream socket.
//
// Lifecycle:
//   - Client connects, ProxySession is created.
//   - RunAsync opens the initial upstream and runs c->s and s->c byte pumps until either
//     side closes or one of the transfer requests fires.
//   - On a seamless transfer the upstream is swapped under the client. The client TCP never
//     sees a FIN. See ProxySession.Seamless.cs.
//   - On a redirect the proxy forges a Packet_ServerRedirect to the client and closes. The
//     client reconnects through the proxy and is routed to the target by sticky route. See
//     ProxySession.Redirect.cs.
internal sealed partial class ProxySession : IPlayer
{
    public long Id { get; }

    private readonly ProxyConfig cfg;
    private readonly TcpClient client;
    private readonly NetworkStream clientStream;
    private readonly CancellationToken sessionStopToken;
    private readonly SessionState? state;
    private readonly FrameSniffer? sniffC2S;
    private readonly FrameSniffer? sniffS2C;
    private readonly StickyRouteTable? stickies;
    private readonly IRegistryClient? registry;
    private readonly UdpRouteOverrides? udpOverrides;
    private readonly EventBus? events;

    private TcpClient? upstream;
    private BackendEndpoint? currentBackend;
    private CancellationTokenSource? pumpCts;
    private Task? pumpC2S;
    private Task? pumpS2C;

    private byte[]? capturedIdentification;
    private string? capturedPlayerUid;
    private string? capturedPlayerName;
    private readonly object swapLock = new();
    private volatile bool swapping;
    private volatile bool closed;

    private long c2sBytes;
    private long s2cBytes;

    public ProxySession(long id, ProxyConfig cfg, TcpClient client, CancellationToken stopToken,
        StickyRouteTable? stickies = null, IRegistryClient? registry = null, UdpRouteOverrides? udpOverrides = null,
        EventBus? events = null)
    {
        Id = id;
        this.cfg = cfg;
        this.client = client;
        this.client.NoDelay = true;
        this.clientStream = client.GetStream();
        this.sessionStopToken = stopToken;
        this.stickies = stickies;
        this.registry = registry;
        this.udpOverrides = udpOverrides;
        this.events = events;

        // Sniffers always run on the client-side stream so they can capture the Identification
        // frame even when SniffFrames is disabled in config (we need the bytes for seamless).
        this.state = new SessionState(id);
        this.sniffC2S = new FrameSniffer(id, "c->s", state) { Verbose = cfg.Logging.SniffFrames };
        this.sniffS2C = new FrameSniffer(id, "s->c", state) { Verbose = cfg.Logging.SniffFrames };
        this.sniffC2S.OnRawFrame = OnClientFrame;
    }

    public SessionState.Phase Phase => state?.Current ?? SessionState.Phase.TcpOpen;

    public string? PlayerUid => capturedPlayerUid;
    public string? PlayerName => capturedPlayerName;
    public bool HasIdentification => capturedIdentification != null;
    public string ClientRemote => client.Client.RemoteEndPoint?.ToString() ?? "?";

    // IPlayer surface (aliases over the existing internal fields so handlers get a stable API).
    string? IPlayer.Uid => capturedPlayerUid;
    string? IPlayer.Name => capturedPlayerName;
    IServerInfo? IPlayer.CurrentServer => currentBackend == null ? null : ServerInfo.From(currentBackend);

    Task<string?> IPlayer.TransferAsync(IServerInfo target, string? reason)
        => ((IPlayer)this).TransferAsync(target, cfg.Transfers.DefaultMode, reason);

    async Task<string?> IPlayer.TransferAsync(IServerInfo target, string mode, string? reason)
    {
        var ep = (target as ServerInfo)?.ToEndpoint()
                 ?? new BackendEndpoint { Host = target.Host, Port = target.Port, ServerId = target.ServerId };
        string normalized = string.Equals(mode, "splice", StringComparison.OrdinalIgnoreCase) ? "seamless" : mode;
        if (string.Equals(normalized, "seamless", StringComparison.OrdinalIgnoreCase))
        {
            if (!cfg.Transfers.AllowSeamless) return "seamless transfers disabled in config";
            return await RequestSeamlessAsync(ep, registry, reason, cfg.Registry.FailOnError).ConfigureAwait(false);
        }
        if (string.Equals(normalized, "redirect", StringComparison.OrdinalIgnoreCase))
            return await RequestRedirectAsync(ep, registry, reason, cfg.Registry.FailOnError).ConfigureAwait(false);
        return $"unknown transfer mode '{mode}'";
    }

    void IPlayer.Disconnect(string? reason)
    {
        if (!string.IsNullOrEmpty(reason)) Log.Info($"[s{Id}] disconnect requested by handler: {reason}");
        Close();
    }

    // Real client endpoint as seen by this proxy. Forwarded to backends via reservation.
    private (string ip, int port) ClientEndpoint
    {
        get
        {
            try
            {
                if (client.Client?.RemoteEndPoint is System.Net.IPEndPoint ep)
                {
                    var addr = ep.Address;
                    // Unwrap ::ffff:1.2.3.4 so backends see a clean IPv4 string under dual-stack.
                    if (addr.IsIPv4MappedToIPv6) addr = addr.MapToIPv4();
                    return (addr.ToString(), ep.Port);
                }
            }
            catch { }
            return ("", 0);
        }
    }

    // Force-close. Pumps and sockets tear down on the next loop iteration.
    public void Close()
    {
        closed = true;
        try { pumpCts?.Cancel(); } catch { }
        try { upstream?.Close(); } catch { }
        try { client.Close(); } catch { }
    }

    private void OnClientFrame(string name, ReadOnlyMemory<byte> raw)
    {
        if (capturedIdentification != null) return;
        if (name != "Identification") return;

        capturedIdentification = raw.ToArray();
        if (!IdentificationParser.TryExtract(capturedIdentification, out var uid, out var pname))
        {
            Log.Warn($"[s{Id}] captured Identification frame ({capturedIdentification.Length} bytes) but could not parse PlayerUID, registry-backed transfers disabled for this session");
            return;
        }

        capturedPlayerUid = uid;
        capturedPlayerName = pname;
        Log.Info($"[s{Id}] captured Identification frame ({capturedIdentification.Length} bytes) player='{pname}' uid={uid}");

        if (stickies == null || string.IsNullOrEmpty(uid)) return;
        if (!stickies.TryConsume(uid, out var stickyTarget, out var stickyReason)) return;

        // Replaying Identification to the same backend trips its duplicate-login path.
        if (currentBackend is BackendEndpoint cur &&
            string.Equals(cur.Host, stickyTarget.Host, StringComparison.OrdinalIgnoreCase) &&
            cur.Port == stickyTarget.Port)
        {
            Log.Info($"[s{Id}] sticky route hit: uid={uid} -> {stickyTarget} but already on this backend; skipping");
            return;
        }

        Log.Info($"[s{Id}] sticky route hit: uid={uid} -> {stickyTarget} (reason='{stickyReason}')");
        _ = Task.Run(async () =>
        {
            try
            {
                var fail = await RequestSeamlessAsync(stickyTarget, registry,
                    $"sticky reconnect: {stickyReason}", failOnRegistryError: false).ConfigureAwait(false);
                if (fail != null)
                    Log.Warn($"[s{Id}] sticky reconnect splice failed: {fail} (session will continue on default backend)");
            }
            catch (Exception ex) { Log.Warn($"[s{Id}] sticky reconnect splice crashed: {ex.Message}"); }
        }, sessionStopToken);
    }

    public async Task RunAsync(IReadOnlyList<BackendEndpoint> tryOrder)
    {
        var remote = client.Client.RemoteEndPoint?.ToString() ?? "?";
        Log.Info($"[s{Id}] client connected from {remote}");
        try
        {
            // Try each candidate until one connects. ConnectUpstreamAsync fires per-attempt
            // Handler cancel stops the chain. Connect failure tries the next backend.
            bool connected = false;
            string? lastFailReason = null;
            for (int i = 0; i < tryOrder.Count; i++)
            {
                var (ok, cancelled, reason) = await ConnectUpstreamAsync(tryOrder[i]).ConfigureAwait(false);
                if (ok) { connected = true; break; }
                lastFailReason = reason;
                if (cancelled) break;
                if (i + 1 < tryOrder.Count)
                    Log.Info($"[s{Id}] failover: trying next candidate after '{reason}'");
            }
            if (!connected)
            {
                Log.Warn($"[s{Id}] no candidate connected: {lastFailReason ?? "unknown"}; sending forged disconnect");
                try
                {
                    var frame = DisconnectBuilder.BuildDisconnectFrame($"No backend reachable right now ({lastFailReason ?? "all candidates failed"}). Please try again shortly.");
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await clientStream.WriteAsync(frame, cts.Token).ConfigureAwait(false);
                    await clientStream.FlushAsync(cts.Token).ConfigureAwait(false);
                    try { await Task.Delay(150, cts.Token).ConfigureAwait(false); } catch { }
                }
                catch { }
                return;
            }

            // Loop: run pumps until they exit. A seamless transfer restarts them on a new upstream.
            while (!sessionStopToken.IsCancellationRequested && !closed)
            {
                await Task.WhenAll(SafeAwait(pumpC2S!), SafeAwait(pumpS2C!)).ConfigureAwait(false);
                if (!swapping) break;  // pumps ended because of client/upstream close, not a swap

                // The swap routine installs the new pumps before it clears this flag.
                while (swapping && !sessionStopToken.IsCancellationRequested && !closed)
                    await Task.Delay(10, sessionStopToken).ConfigureAwait(false);
            }
        }
        finally
        {
            closed = true;
            try { upstream?.Close(); } catch { }
            try { client.Close(); } catch { }
            // Drop any UDP retargeting for this client IP so the next player (or NAT reuse) starts fresh.
            try
            {
                if (udpOverrides != null && client.Client?.RemoteEndPoint is System.Net.IPEndPoint ep)
                    udpOverrides.Clear(ep.Address);
            }
            catch { }
            Log.Info($"[s{Id}] session closed (c->s {c2sBytes} bytes, s->c {s2cBytes} bytes)");
            if (events != null)
            {
                try { await events.FireAsync(new PlayerDisconnectEvent(this, c2sBytes, s2cBytes)).ConfigureAwait(false); }
                catch { }
            }
        }
    }

    // Single-target convenience. Kept for callers that already have one endpoint in hand.
    public Task RunAsync(BackendEndpoint initial) => RunAsync(new[] { initial });

    private async Task<(bool ok, bool cancelled, string? reason)> ConnectUpstreamAsync(BackendEndpoint target)
    {
        // ServerPreConnect: handlers can swap target or cancel before we open the socket.
        if (events != null)
        {
            var pre = new ServerPreConnectEvent(this, ServerInfo.From(target), reason: "initial connect");
            await events.FireAsync(pre).ConfigureAwait(false);
            if (pre.IsCancelled)
            {
                Log.Warn($"[s{Id}] initial upstream cancelled by handler: {pre.CancelReason}");
                return (false, true, pre.CancelReason ?? "cancelled");
            }
            if (pre.Target is ServerInfo si) target = si.ToEndpoint();
        }

        var previous = currentBackend == null ? null : ServerInfo.From(currentBackend);
        var up = new TcpClient { NoDelay = true };
        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(sessionStopToken);
        connectCts.CancelAfter(cfg.Advanced.ConnectTimeoutMs);
        ProxyMetrics.BackendConnectAttempt();
        try
        {
            await up.ConnectAsync(target.Host, target.Port, connectCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ProxyMetrics.BackendConnectFailure();
            Log.Warn($"[s{Id}] could not reach backend {target}: {ex.Message}");
            try { up.Close(); } catch { }
            return (false, false, $"{target}: {ex.Message}");
        }
        if (!await TryWriteProxyProtocolAsync(up, target).ConfigureAwait(false))
        {
            ProxyMetrics.BackendConnectFailure();
            try { up.Close(); } catch { }
            return (false, false, $"{target}: PROXY v2 header write failed");
        }
        ProxyMetrics.BackendConnectSuccess();
        Log.Info($"[s{Id}] upstream connected to {target}");
        upstream = up;
        currentBackend = target;
        UpdateUdpOverride(target);
        StartPumps();
        if (events != null)
        {
            try { await events.FireAsync(new ServerPostConnectEvent(this, ServerInfo.From(target), previous)).ConfigureAwait(false); }
            catch { }
        }
        return (true, false, null);
    }

    // Pin UDP for this client to the same backend our TCP session uses. No-op without overrides.
    private void UpdateUdpOverride(BackendEndpoint target)
    {
        if (udpOverrides == null) return;
        try
        {
            if (client.Client?.RemoteEndPoint is System.Net.IPEndPoint ep)
                udpOverrides.Set(ep.Address, target);
        }
        catch { }
    }

    // PROXY v2 has to be the first upstream bytes.
    private async Task<bool> TryWriteProxyProtocolAsync(TcpClient up, BackendEndpoint target)
    {
        if (!target.ProxyProtocol) return true;
        if (client.Client?.RemoteEndPoint is not System.Net.IPEndPoint clientEp ||
            up.Client?.LocalEndPoint is not System.Net.IPEndPoint upstreamEp)
        {
            Log.Warn($"[s{Id}] proxy-protocol header skipped for {target}: endpoint info unavailable");
            return true;
        }
        try
        {
            var header = ProxyProtocolV2.BuildHeader(clientEp, upstreamEp);
            await up.GetStream().WriteAsync(header, sessionStopToken).ConfigureAwait(false);
            Log.Trace($"[s{Id}] wrote PROXY v2 header ({header.Length}B) {clientEp} -> {upstreamEp} for {target}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"[s{Id}] PROXY v2 header write failed for {target}: {ex.Message}");
            return false;
        }
    }

    private void StartPumps()
    {
        pumpCts = CancellationTokenSource.CreateLinkedTokenSource(sessionStopToken);
        pumpC2S = PumpAsync("c->s", clientStream, upstream!.GetStream(), sniffC2S, isC2S: true, pumpCts.Token);
        pumpS2C = PumpAsync("s->c", upstream!.GetStream(), clientStream, sniffS2C, isC2S: false, pumpCts.Token);
    }

    private async Task PumpAsync(string label, NetworkStream from, NetworkStream to, FrameSniffer? sniffer, bool isC2S, CancellationToken token)
    {
        var buf = new byte[cfg.Advanced.BufferSize];
        long total = 0;
        try
        {
            while (!token.IsCancellationRequested)
            {
                int read;
                try { read = await from.ReadAsync(buf.AsMemory(0, buf.Length), token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                catch (IOException) { break; }
                catch (ObjectDisposedException) { break; }
                if (read <= 0) break;

                total += read;
                try { await to.WriteAsync(buf.AsMemory(0, read), token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                catch (IOException) { break; }
                catch (ObjectDisposedException) { break; }

                sniffer?.OnBytes(new ReadOnlySpan<byte>(buf, 0, read));
            }
        }
        finally
        {
            if (isC2S) Interlocked.Add(ref c2sBytes, total); else Interlocked.Add(ref s2cBytes, total);
            if (isC2S) ProxyMetrics.AddBytes(total, 0); else ProxyMetrics.AddBytes(0, total);
            Log.Trace($"[s{Id}] {label} pump exited ({total} bytes this segment)");
        }
    }

    private static async Task SafeAwait(Task t) { try { await t.ConfigureAwait(false); } catch { } }
}
