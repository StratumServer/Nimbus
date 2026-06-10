using System.Net.Sockets;

namespace Nimbus.Proxy;

// A single proxied player session. Owns the client socket and the current upstream socket.
//
// Lifecycle:
//   - Client connects, ProxySession is created.
//   - RunAsync opens the initial upstream and runs c->s and s->c byte pumps until either
//     side closes or one of the transfer requests fires.
//   - On redirect, the proxy forges a Packet_ServerRedirect and closes. The reconnect is
//     routed by sticky UID before the new upstream is opened.
//   - On seamless, the normal path uses the same safe redirect underneath while Nimbus.Client
//     hides the VS loading UI. Raw upstream splice lives behind an unsafe config flag.
internal sealed partial class ProxySession : IPlayer
{
    public long Id { get; }

    private readonly ProxyConfig cfg;
    private readonly TcpClient client;
    private readonly NetworkStream clientStream;
    private readonly CancellationToken sessionStopToken;
    private readonly DateTimeOffset sessionStart = DateTimeOffset.UtcNow;
    private readonly string clientRemote; // captured at construction — safe after socket close
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
    private volatile bool seamlessCapable;
    private readonly object swapLock = new();
    private volatile bool swapping;
    private volatile bool closed;

    private long c2sBytes;
    private long s2cBytes;
    private volatile bool kickedByBackend;

    // Initial-join reservation state for the currently connected backend:
    //   0 = pending, 1 = done (or not needed), 2 = failed terminal.
    private int initialReservationState;

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
        this.clientRemote = client.Client?.RemoteEndPoint is System.Net.IPEndPoint cep
            ? (cep.Address.IsIPv4MappedToIPv6 ? cep.Address.MapToIPv4().ToString() : cep.Address.ToString())
            : "?";
        this.stickies = stickies;
        this.registry = registry;
        this.udpOverrides = udpOverrides;
        this.events = events;

        // Sniffers always run on the client stream so registry-backed joins and transfers have
        // the player UID even when SniffFrames is disabled.
        this.state = new SessionState(id);
        this.sniffC2S = new FrameSniffer(id, "c->s", state) { Verbose = cfg.Logging.SniffFrames };
        this.sniffS2C = new FrameSniffer(id, "s->c", state) { Verbose = cfg.Logging.SniffFrames };
        this.sniffC2S.OnRawFrame = OnClientFrame;
    }

    public SessionState.Phase Phase => state?.Current ?? SessionState.Phase.TcpOpen;

    public string? PlayerUid => capturedPlayerUid;
    public string? PlayerName => capturedPlayerName;
    public bool HasIdentification => capturedIdentification != null;
    public bool SupportsSeamlessTransfers => seamlessCapable;
    public string ClientRemote => clientRemote;

    // IPlayer surface (aliases over the existing internal fields so handlers get a stable API).
    string? IPlayer.Uid => capturedPlayerUid;
    string? IPlayer.Name => capturedPlayerName;
    IServerInfo? IPlayer.CurrentServer => currentBackend == null ? null : currentBackend.ToServerInfo();
    bool IPlayer.SupportsSeamlessTransfers => SupportsSeamlessTransfers;

    Task<string?> IPlayer.TransferAsync(IServerInfo target, string? reason)
        => ((IPlayer)this).TransferAsync(target, cfg.Transfers.DefaultMode, reason);

    async Task<string?> IPlayer.TransferAsync(IServerInfo target, string mode, string? reason)
        => (await RequestTransferAsync(target.ToEndpoint(), mode, registry, reason, cfg.Registry.FailOnError).ConfigureAwait(false)).failReason;

    internal async Task<(string modeUsed, string? failReason)> RequestTransferAsync(BackendEndpoint target, string mode,
        IRegistryClient? registry = null, string? reason = null, bool failOnRegistryError = true)
    {
        string normalized = string.Equals(mode, "splice", StringComparison.OrdinalIgnoreCase) ? "seamless" : mode;
        if (string.Equals(normalized, "seamless", StringComparison.OrdinalIgnoreCase))
        {
            if (!cfg.Transfers.AllowSeamless)
                return ("seamless", "seamless transfers disabled in config");

            if (Phase != SessionState.Phase.Ready)
                return ("seamless", $"seamless requires a fully joined session (phase=Ready). current phase={Phase}");

            if (cfg.Transfers.RequireSeamlessCapability && !SupportsSeamlessTransfers)
            {
                if (!cfg.Transfers.FallbackToRedirectWhenSeamlessUnavailable)
                    return ("seamless", "client has not advertised Nimbus seamless capability");

                Log.Warn($"[s{Id}] seamless requested but client has no Nimbus capability; falling back to redirect");
                var redirectFail = await RequestRedirectAsync(target, registry,
                    reason ?? "seamless unavailable, redirect fallback", failOnRegistryError).ConfigureAwait(false);
                return ("redirect", redirectFail);
            }

            if (!cfg.Transfers.EnableUnsafeSeamlessSplice)
            {
                var redirectFail = await RequestRedirectAsync(target, registry,
                    reason ?? "seamless visual redirect", failOnRegistryError).ConfigureAwait(false);
                return ("seamless", redirectFail);
            }

            return ("seamless", await RequestSeamlessAsync(target, registry, reason, failOnRegistryError).ConfigureAwait(false));
        }
        if (string.Equals(normalized, "redirect", StringComparison.OrdinalIgnoreCase))
            return ("redirect", await RequestRedirectAsync(target, registry, reason, failOnRegistryError).ConfigureAwait(false));
        return (normalized, $"unknown transfer mode '{mode}'");
    }

    internal void MarkSeamlessCapable()
    {
        seamlessCapable = true;
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
        if (name != "Identification") return;

        CaptureIdentification(raw.Span, source: "sniffer");
    }

    private bool CaptureIdentification(ReadOnlySpan<byte> raw, string source)
    {
        if (capturedIdentification != null) return true;

        var frame = raw.ToArray();
        if (!IdentificationParser.TryExtract(frame, out var uid, out var pname))
        {
            Log.Warn($"[s{Id}] captured Identification frame from {source} ({frame.Length} bytes) but could not parse PlayerUID, registry-backed transfers disabled for this session");
            return false;
        }

        capturedIdentification = frame;
        capturedPlayerUid = uid;
        capturedPlayerName = pname;
        TryConsumeStickyRoute(uid);
        return true;
    }

    private void TryConsumeStickyRoute(string uid)
    {
        if (stickies == null || string.IsNullOrEmpty(uid)) return;
        if (!stickies.TryConsume(uid, out var stickyTarget, out var stickyReason)) return;

        // Replaying Identification to the same backend trips its duplicate-login path.
        if (currentBackend is BackendEndpoint cur &&
            string.Equals(cur.Host, stickyTarget.Host, StringComparison.OrdinalIgnoreCase) &&
            cur.Port == stickyTarget.Port)
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                var fail = await RequestSeamlessAsync(stickyTarget, registry,
                    $"sticky reconnect: {stickyReason}", failOnRegistryError: false).ConfigureAwait(false);
                if (fail != null)
                    Log.Warn($"[s{Id}] sticky reconnect transfer failed: {fail} (session will continue on default backend)");
            }
            catch (Exception ex) { Log.Warn($"[s{Id}] sticky reconnect transfer crashed: {ex.Message}"); }
        }, sessionStopToken);
    }

    public async Task RunAsync(IReadOnlyList<BackendEndpoint> tryOrder, ReadOnlyMemory<byte> firstClientFrame = default)
    {
        try
        {
            // Try each candidate until one connects. ConnectUpstreamAsync fires per-attempt
            // Handler cancel stops the chain. Connect failure tries the next backend.
            bool connected = false;
            string? lastFailReason = null;
            for (int i = 0; i < tryOrder.Count; i++)
            {
                var (ok, cancelled, reason) = await ConnectUpstreamAsync(tryOrder[i], firstClientFrame).ConfigureAwait(false);
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

            // Loop until the pumps exit. The unsafe splice path restarts them on a new upstream.
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
            if (events != null)
            {
                if (kickedByBackend && currentBackend != null)
                {
                    try { await events.FireAsync(new ServerKickedEvent(this, currentBackend.ToServerInfo())).ConfigureAwait(false); }
                    catch { }
                }
                try { await events.FireAsync(new PlayerDisconnectEvent(this, c2sBytes, s2cBytes)).ConfigureAwait(false); }
                catch { }
            }
            var elapsed = DateTimeOffset.UtcNow - sessionStart;
            Log.Info($"[s{Id}] {capturedPlayerName ?? clientRemote} disconnected ({FormatDuration(elapsed)} | ↑{FormatBytes(c2sBytes)} ↓{FormatBytes(s2cBytes)})");
        }
    }

    // Single-target convenience. Kept for callers that already have one endpoint in hand.
    public Task RunAsync(BackendEndpoint initial) => RunAsync(new[] { initial });

    private async Task<(bool ok, bool cancelled, string? reason)> ConnectUpstreamAsync(BackendEndpoint target, ReadOnlyMemory<byte> firstClientFrame)
    {
        // ServerPreConnect: handlers can swap target or cancel before we open the socket.
        if (events != null)
        {
            var pre = new ServerPreConnectEvent(this, target.ToServerInfo(), reason: "initial connect");
            await events.FireAsync(pre).ConfigureAwait(false);
            if (pre.IsCancelled)
            {
                Log.Warn($"[s{Id}] initial upstream cancelled by handler: {pre.CancelReason}");
                return (false, true, pre.CancelReason ?? "cancelled");
            }
            target = pre.Target.ToEndpoint();
        }

        if (!firstClientFrame.IsEmpty)
        {
            CaptureIdentification(firstClientFrame.Span, source: "first frame");
            // If the first frame already contained Identification, prime the reservation
            // before replaying bytes upstream. Missing UID here is non-fatal; the c->s pump
            // retries as soon as it captures Identification from later frames.
            var mintFail = await EnsureInitialReservationAsync(target, "initial connect").ConfigureAwait(false);
            if (mintFail != null)
            {
                Log.Warn($"[s{Id}] initial reservation mint failed for {target}: {mintFail}");
                return (false, true, mintFail);
            }
        }

        var previous = currentBackend == null ? null : currentBackend.ToServerInfo();
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
        if (!await TryWriteFirstClientFrameAsync(up, firstClientFrame).ConfigureAwait(false))
        {
            ProxyMetrics.BackendConnectFailure();
            try { up.Close(); } catch { }
            return (false, false, $"{target}: first client frame write failed");
        }
        ProxyMetrics.BackendConnectSuccess();
        upstream = up;
        currentBackend = target;
        UpdateUdpOverride(target);
        StartPumps();
        Log.Info($"[s{Id}] {capturedPlayerName ?? "?"} ({clientRemote}) → {target.ServerId ?? target.ToString()}");
        if (events != null)
        {
            try { await events.FireAsync(new ServerPostConnectEvent(this, target.ToServerInfo(), previous)).ConfigureAwait(false); }
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

    private async Task<bool> TryWriteFirstClientFrameAsync(TcpClient up, ReadOnlyMemory<byte> frame)
    {
        if (frame.IsEmpty) return true;
        try
        {
            await up.GetStream().WriteAsync(frame, sessionStopToken).ConfigureAwait(false);
            sniffC2S?.OnBytes(frame.Span);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"[s{Id}] failed to replay first client frame: {ex.Message}");
            return false;
        }
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

                // Parse c->s frames before forwarding so we can mint the initial
                // reservation as soon as Identification is captured.
                if (isC2S)
                {
                    sniffer?.OnBytes(new ReadOnlySpan<byte>(buf, 0, read));
                    if (initialReservationState == 0)
                    {
                        var mintFail = await EnsureInitialReservationAsync(currentBackend, "initial connect (stream)").ConfigureAwait(false);
                        if (mintFail != null)
                        {
                            Log.Warn($"[s{Id}] closing session after reservation prime failed: {mintFail}");
                            break;
                        }
                    }
                }

                try { await to.WriteAsync(buf.AsMemory(0, read), token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                catch (IOException) { break; }
                catch (ObjectDisposedException) { break; }

                if (!isC2S)
                    sniffer?.OnBytes(new ReadOnlySpan<byte>(buf, 0, read));
            }
        }
        finally
        {
            if (isC2S) Interlocked.Add(ref c2sBytes, total); else Interlocked.Add(ref s2cBytes, total);
            if (isC2S) ProxyMetrics.AddBytes(total, 0); else ProxyMetrics.AddBytes(0, total);
            Log.Trace($"[s{Id}] {label} pump exited ({total} bytes this segment)");

            // s->c pump exiting without our own Close() or a swap in flight means the backend
            // dropped the connection while the player was live.
            if (!isC2S && !closed && !swapping)
            {
                var ph = Phase;
                if (ph == SessionState.Phase.Ready || ph == SessionState.Phase.Disconnecting)
                    kickedByBackend = true;
            }
        }
    }

    private async Task<string?> EnsureInitialReservationAsync(BackendEndpoint? target, string reason)
    {
        if (initialReservationState != 0) return null;
        if (target == null)
            return null;
        if (registry == null || string.IsNullOrEmpty(target.ServerId))
        {
            initialReservationState = 1;
            return null;
        }
        if (string.IsNullOrEmpty(capturedPlayerUid))
            return null; // wait until Identification is captured

        var mintFail = await MintReservationIfPossibleAsync(target, registry, reason, cfg.Registry.FailOnError).ConfigureAwait(false);
        if (mintFail != null)
        {
            initialReservationState = 2;
            return mintFail;
        }

        initialReservationState = 1;
        return null;
    }

    private static async Task SafeAwait(Task t) { try { await t.ConfigureAwait(false); } catch { } }

    private static string FormatDuration(TimeSpan t)
    {
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h{t.Minutes:D2}m";
        if (t.TotalMinutes >= 1) return $"{(int)t.TotalMinutes}m{t.Seconds:D2}s";
        return $"{(int)t.TotalSeconds}s";
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1_048_576) return $"{bytes / 1_048_576.0:F1}MB";
        if (bytes >= 1024) return $"{bytes / 1024.0:F1}KB";
        return $"{bytes}B";
    }
}
