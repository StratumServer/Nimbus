using System.Collections.Concurrent;
using System.Net.Sockets;

namespace Nimbus.Proxy;

// A single proxied player session. Owns the client socket and the current upstream socket,
// and can swap the upstream out under a live client.
//
// Lifecycle:
//   - Client connects, ProxySession is created.
//   - RunAsync opens the initial upstream and runs c->s and s->c byte pumps until either
//     side closes or RequestSwapAsync runs.
//   - On swap: dial a new upstream, replay the captured Identification frame against it,
//     then restart the pumps against the new socket. The client sees no FIN on its socket.
//
// Swap is best-effort. In-flight client bytes between read+write may be dropped. The intended
// use is right after Ready, before heavy traffic.
internal sealed class ProxySession
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
    private readonly RegistryClient? registry;
    private readonly UdpRouteOverrides? udpOverrides;

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
        StickyRouteTable? stickies = null, RegistryClient? registry = null, UdpRouteOverrides? udpOverrides = null)
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

        // Sniffers always run on the client-side stream so they can capture the Identification
        // frame even when SniffFrames is disabled in config (we need the bytes for swap).
        this.state = new SessionState(id);
        this.sniffC2S = new FrameSniffer(id, "c->s", state) { Verbose = cfg.SniffFrames };
        this.sniffS2C = new FrameSniffer(id, "s->c", state) { Verbose = cfg.SniffFrames };
        this.sniffC2S.OnRawFrame = OnClientFrame;
    }

    public SessionState.Phase Phase => state?.Current ?? SessionState.Phase.TcpOpen;

    public string? PlayerUid => capturedPlayerUid;
    public string? PlayerName => capturedPlayerName;
    public bool HasIdentification => capturedIdentification != null;
    public string ClientRemote => client.Client.RemoteEndPoint?.ToString() ?? "?";

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
                    // Unwrap ::ffff:1.2.3.4 to 1.2.3.4 so backends see a clean IPv4 string when the
                    // proxy is listening on a dual-stack socket.
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
        if (name == "Identification")
        {
            capturedIdentification = raw.ToArray();
            if (IdentificationParser.TryExtract(capturedIdentification, out var uid, out var pname))
            {
                capturedPlayerUid = uid;
                capturedPlayerName = pname;
                Log.Info($"[s{Id}] captured Identification frame ({capturedIdentification.Length} bytes) player='{pname}' uid={uid}, eligible for swap");

                // If an operator staged a sticky route for this uid (via the disconnect-transfer
                // path), redirect this fresh session to that backend now. Fire-and-forget so we
                // don't block the c->s pump that just delivered the Identification frame.
                if (stickies != null && !string.IsNullOrEmpty(uid) &&
                    stickies.TryConsume(uid, out var stickyTarget, out var stickyReason))
                {
                    // If the sticky target is the backend we're already connected to (the
                    // operator redirected back to where we landed), don't swap. Opening a second
                    // TCP to the same backend with the same Identification trips its "player
                    // joined again, killing previous client" path and both die.
                    if (currentBackend is BackendEndpoint cur &&
                        string.Equals(cur.Host, stickyTarget.Host, StringComparison.OrdinalIgnoreCase) &&
                        cur.Port == stickyTarget.Port)
                    {
                        Log.Info($"[s{Id}] sticky route hit: uid={uid} -> {stickyTarget} but already on this backend; skipping swap");
                    }
                    else
                    {
                        Log.Info($"[s{Id}] sticky route hit: uid={uid} -> {stickyTarget} (staged reason='{stickyReason}')");
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                var fail = await RequestSwapAsync(stickyTarget, registry,
                                    $"sticky reconnect: {stickyReason}",
                                    failOnRegistryError: false).ConfigureAwait(false);
                                if (fail != null)
                                    Log.Warn($"[s{Id}] sticky swap failed: {fail} (session will continue on default backend)");
                            }
                            catch (Exception ex) { Log.Warn($"[s{Id}] sticky swap crashed: {ex.Message}"); }
                        }, sessionStopToken);
                    }
                }
            }
            else
            {
                Log.Warn($"[s{Id}] captured Identification frame ({capturedIdentification.Length} bytes) but could not parse PlayerUID, registry-backed swap disabled for this session");
            }
        }
    }

    public async Task RunAsync(BackendEndpoint initial)
    {
        var remote = client.Client.RemoteEndPoint?.ToString() ?? "?";
        Log.Info($"[s{Id}] client connected from {remote}");
        try
        {
            if (!await ConnectUpstreamAsync(initial).ConfigureAwait(false)) return;

            // Loop: run pumps until they exit. Swap restarts them with a new upstream.
            while (!sessionStopToken.IsCancellationRequested && !closed)
            {
                await Task.WhenAll(SafeAwait(pumpC2S!), SafeAwait(pumpS2C!)).ConfigureAwait(false);
                if (!swapping) break;  // pumps ended because of client/upstream close, not a swap
                // Pumps ended because of a swap. Wait for the swap routine to finish installing
                // the new pumps (it clears `swapping` only AFTER StartPumps). Otherwise we'd race
                // and re-await the already-completed old pump references, causing premature exit.
                while (swapping && !sessionStopToken.IsCancellationRequested && !closed)
                {
                    await Task.Delay(10, sessionStopToken).ConfigureAwait(false);
                }
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
        }
    }

    private async Task<bool> ConnectUpstreamAsync(BackendEndpoint target)
    {
        var up = new TcpClient { NoDelay = true };
        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(sessionStopToken);
        connectCts.CancelAfter(cfg.ConnectTimeoutMs);
        try
        {
            await up.ConnectAsync(target.Host, target.Port, connectCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn($"[s{Id}] could not reach backend {target}: {ex.Message}");
            return false;
        }
        Log.Info($"[s{Id}] upstream connected to {target}");
        upstream = up;
        currentBackend = target;
        UpdateUdpOverride(target);
        StartPumps();
        return true;
    }

    // Register the current backend for UDP routing so the relay sends position/voice traffic to
    // the same backend our TCP session uses. No-op when overrides aren't wired (unit tests).
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

    private void StartPumps()
    {
        pumpCts = CancellationTokenSource.CreateLinkedTokenSource(sessionStopToken);
        pumpC2S = PumpAsync("c->s", clientStream, upstream!.GetStream(), sniffC2S, isC2S: true, pumpCts.Token);
        pumpS2C = PumpAsync("s->c", upstream!.GetStream(), clientStream, sniffS2C, isC2S: false, pumpCts.Token);
    }

    private async Task PumpAsync(string label, NetworkStream from, NetworkStream to, FrameSniffer? sniffer, bool isC2S, CancellationToken token)
    {
        var buf = new byte[cfg.BufferSize];
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
            Log.Trace($"[s{Id}] {label} pump exited ({total} bytes this segment)");
        }
    }

    // Swap this session's upstream to `target`. Returns null on success, or a short reason
    // string on failure. Requires that we've captured the client's Identification frame.
    //
    // If `registry` is non-null and `target.ServerId` is set, a reservation is minted on the
    // registry before connecting the new upstream so the target backend's identification gate
    // accepts the player by UID. If the mint fails and `failOnRegistryError` is true, the swap
    // is aborted.
    public async Task<string?> RequestSwapAsync(BackendEndpoint target, RegistryClient? registry = null, string? swapReason = null, bool failOnRegistryError = true)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        if (closed) { Log.Warn($"[s{Id}] swap rejected: session is closed"); return "session closed"; }
        if (capturedIdentification == null)
        {
            Log.Warn($"[s{Id}] swap rejected: no Identification captured yet (phase={Phase})");
            return $"no Identification captured yet (phase={Phase})";
        }

        TcpClient? oldUpstream;
        CancellationTokenSource? oldCts;
        Task? oldPumpC2S;
        Task? oldPumpS2C;
        lock (swapLock)
        {
            if (swapping) { Log.Warn($"[s{Id}] swap already in progress"); return "swap already in progress"; }
            swapping = true;
            oldUpstream = upstream;
            oldCts = pumpCts;
            oldPumpC2S = pumpC2S;
            oldPumpS2C = pumpS2C;
        }

        Log.Info($"[s{Id}] SWAP -> {target} (phase {Phase}, captured ident {capturedIdentification.Length}B, reason='{swapReason ?? "<none>"}')");

        // Pre-mint a Nimbus reservation so the target backend accepts the replayed
        // Identification by UID instead of re-running Stratum auth.
        if (registry != null && !string.IsNullOrEmpty(target.ServerId))
        {
            if (string.IsNullOrEmpty(capturedPlayerUid))
            {
                if (failOnRegistryError)
                {
                    Log.Warn($"[s{Id}] swap aborted: no PlayerUID parsed from captured Identification");
                    swapping = false;
                    return "no PlayerUID parsed from Identification";
                }
                Log.Warn($"[s{Id}] proceeding without reservation: no PlayerUID parsed from Identification");
            }
            else
            {
                using var mintCts = CancellationTokenSource.CreateLinkedTokenSource(sessionStopToken);
                mintCts.CancelAfter(TimeSpan.FromSeconds(10));
                var reservation = await registry.MintReservationAsync(
                    capturedPlayerUid!, capturedPlayerName ?? "", target.ServerId, swapReason ?? "proxy swap", mintCts.Token,
                    ClientEndpoint.ip, ClientEndpoint.port)
                    .ConfigureAwait(false);
                if (reservation == null)
                {
                    if (failOnRegistryError)
                    {
                        Log.Warn($"[s{Id}] swap aborted: registry mint failed for uid={capturedPlayerUid} target={target.ServerId}");
                        swapping = false;
                        return "registry mint failed";
                    }
                    Log.Warn($"[s{Id}] proceeding without reservation: registry mint failed");
                }
                else
                {
                    Log.Info($"[s{Id}] reservation minted id={reservation.Id} target={reservation.TargetServerId} ttl={reservation.ExpiresAtUnix - DateTimeOffset.UtcNow.ToUnixTimeSeconds()}s");
                }
            }
        }
        else if (registry != null && string.IsNullOrEmpty(target.ServerId))
        {
            Log.Trace($"[s{Id}] swap: target has no ServerId; skipping reservation mint");
        }

        // Open new upstream.
        var newUp = new TcpClient { NoDelay = true };
        using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(sessionStopToken))
        {
            connectCts.CancelAfter(cfg.ConnectTimeoutMs);
            try { await newUp.ConnectAsync(target.Host, target.Port, connectCts.Token).ConfigureAwait(false); }
            catch (Exception ex)
            {
                Log.Warn($"[s{Id}] swap failed: could not reach {target}: {ex.Message}");
                try { newUp.Close(); } catch { }
                swapping = false;
                return $"connect failed: {ex.Message}";
            }
        }

        // Replay Identification to the new backend so it begins auth/handshake.
        try
        {
            await newUp.GetStream().WriteAsync(capturedIdentification, sessionStopToken).ConfigureAwait(false);
            Log.Info($"[s{Id}] replayed Identification to new backend");
        }
        catch (Exception ex)
        {
            Log.Warn($"[s{Id}] swap failed: write Identification: {ex.Message}");
            try { newUp.Close(); } catch { }
            swapping = false;
            return $"write Identification failed: {ex.Message}";
        }

        // Atomic swap: cancel old pumps first, wait for them to fully exit, then close the old
        // upstream and start fresh pumps. If we don't wait, the old s->c pump can interleave
        // in-flight bytes with the new pump's writes, corrupting the client frame stream and
        // hanging the handshake ("stuck at downloading data").
        try { oldCts?.Cancel(); } catch { }
        try { oldUpstream?.Close(); } catch { } // force read EOF so pumps unblock immediately
        try
        {
            var waitC2S = oldPumpC2S != null ? SafeAwait(oldPumpC2S) : Task.CompletedTask;
            var waitS2C = oldPumpS2C != null ? SafeAwait(oldPumpS2C) : Task.CompletedTask;
            using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(sessionStopToken);
            waitCts.CancelAfter(TimeSpan.FromSeconds(5));
            var waitAll = Task.WhenAll(waitC2S, waitS2C);
            var completed = await Task.WhenAny(waitAll, Task.Delay(Timeout.Infinite, waitCts.Token)).ConfigureAwait(false);
            if (completed != waitAll)
            {
                Log.Warn($"[s{Id}] swap: old pumps did not exit within 5s; proceeding anyway (may cause stream corruption)");
            }
        }
        catch { }

        upstream = newUp;
        currentBackend = target;
        UpdateUdpOverride(target);
        StartPumps();
        swapping = false;  // release the flag AFTER new pumps are installed so RunAsync's spin-wait can re-await them

        Log.Info($"[s{Id}] swap complete; new upstream {target} is live");
        Log.Info($"[s{Id}] AUDIT op=swap target={target} reason='{swapReason ?? ""}' uid={capturedPlayerUid ?? ""} result=ok duration_ms={sw.ElapsedMilliseconds}");
        return null;
    }

    // Redirect-style transfer: pre-mint a reservation for the player on the target, forge a
    // vanilla Packet_ServerRedirect (Id=29) directly to the client, then close the session.
    // The client drops the proxy connection, reconnects to target.Host:target.Port, and the
    // backend's identification gate consumes the reservation (no auth-server round-trip).
    // Unlike RequestSwapAsync, this fully resets client-side world state.
    //
    // Returns null on success, or a short reason string on failure. Does not close the
    // session on failure (caller can retry or fall back to splice).
    public async Task<string?> RequestRedirectAsync(BackendEndpoint target, RegistryClient? registry = null, string? reason = null, bool failOnRegistryError = true)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        if (closed) { Log.Warn($"[s{Id}] redirect rejected: session is closed"); return "session closed"; }
        if (capturedIdentification == null)
        {
            Log.Warn($"[s{Id}] redirect rejected: no Identification captured yet (phase={Phase})");
            return $"no Identification captured yet (phase={Phase})";
        }
        if (string.IsNullOrEmpty(target.Host)) return "redirect target has empty host";
        if (target.Port <= 0 || target.Port > 65535) return $"redirect target has invalid port {target.Port}";

        Log.Info($"[s{Id}] REDIRECT -> {target} (phase {Phase}, reason='{reason ?? "<none>"}')");

        // Pre-mint reservation on the target backend (skip if no registry/ServerId).
        if (registry != null && !string.IsNullOrEmpty(target.ServerId))
        {
            if (string.IsNullOrEmpty(capturedPlayerUid))
            {
                if (failOnRegistryError) { Log.Warn($"[s{Id}] redirect aborted: no PlayerUID parsed"); return "no PlayerUID parsed from Identification"; }
                Log.Warn($"[s{Id}] proceeding without reservation: no PlayerUID parsed");
            }
            else
            {
                using var mintCts = CancellationTokenSource.CreateLinkedTokenSource(sessionStopToken);
                mintCts.CancelAfter(TimeSpan.FromSeconds(10));
                var reservation = await registry.MintReservationAsync(
                    capturedPlayerUid!, capturedPlayerName ?? "", target.ServerId, reason ?? "proxy redirect", mintCts.Token,
                    ClientEndpoint.ip, ClientEndpoint.port)
                    .ConfigureAwait(false);
                if (reservation == null)
                {
                    if (failOnRegistryError) { Log.Warn($"[s{Id}] redirect aborted: registry mint failed for uid={capturedPlayerUid} target={target.ServerId}"); return "registry mint failed"; }
                    Log.Warn($"[s{Id}] proceeding without reservation: registry mint failed");
                }
                else
                {
                    Log.Info($"[s{Id}] reservation minted id={reservation.Id} target={reservation.TargetServerId} ttl={reservation.ExpiresAtUnix - DateTimeOffset.UtcNow.ToUnixTimeSeconds()}s");
                }
            }
        }

        // Stage a sticky route on the proxy's PlayerUID->target table. Required because the
        // RedirectFix client mod (Harmony patch on ClientMain.ExitAndSwitchServer) routes the
        // reconnect through the cached connectData (the proxy address), not the redirect
        // frame's target host. Without a sticky, the reconnect lands on the default backend.
        // With it, OnClientFrame's sticky lookup routes to `target`.
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

        // Forge the Packet_ServerRedirect frame and write it to the client.
        byte[] frame;
        try { frame = RedirectBuilder.BuildRedirectFrame(hostString, displayName); }
        catch (Exception ex) { Log.Warn($"[s{Id}] redirect frame build failed: {ex.Message}"); return $"frame build failed: {ex.Message}"; }

        try
        {
            await clientStream.WriteAsync(frame, sessionStopToken).ConfigureAwait(false);
            await clientStream.FlushAsync(sessionStopToken).ConfigureAwait(false);
            Log.Info($"[s{Id}] redirect frame sent to client ({frame.Length}B) host='{hostString}' name='{displayName}'");
        }
        catch (Exception ex)
        {
            Log.Warn($"[s{Id}] redirect write to client failed: {ex.Message}");
            return $"client write failed: {ex.Message}";
        }

        // Give the client a brief moment to read+act on the frame before we tear down the
        // sockets. 250ms is enough for it to process the in-flight packet and start its own
        // disconnect.
        try { await Task.Delay(250, sessionStopToken).ConfigureAwait(false); } catch { }
        Close();
        Log.Info($"[s{Id}] AUDIT op=redirect target={target} reason='{reason ?? ""}' uid={capturedPlayerUid ?? ""} result=ok duration_ms={sw.ElapsedMilliseconds}");
        return null;
    }

    // Disconnect-style transfer. Stage a sticky route for the player's UID, pre-mint a
    // reservation on the target, then forge a vanilla Packet_ServerDisconnectPlayer (Id=9)
    // with a friendly reason and close the session.
    //
    // On the client, HandleDisconnectPlayer calls DestroyGameSession(gotDisconnected: true)
    // with both early-return flags still false, so Dispose() runs and tears down atlases /
    // items / blocks / tessellator state cleanly. The player lands on the vanilla disconnect
    // screen with the reason text. When they click Reconnect, they hit this proxy again, and
    // OnClientFrame's sticky lookup routes their fresh session to `target` instead of the
    // default backend.
    //
    // `stickyTtl` bounds how long the staged route survives if the player never reconnects
    // (default 5 min). Returns null on success, or a short reason string on failure.
    public async Task<string?> RequestDisconnectAsync(BackendEndpoint target, RegistryClient? registry = null,
        string? reason = null, TimeSpan? stickyTtl = null, bool failOnRegistryError = true)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        if (closed) { Log.Warn($"[s{Id}] disconnect rejected: session is closed"); return "session closed"; }
        if (capturedIdentification == null)
        {
            Log.Warn($"[s{Id}] disconnect rejected: no Identification captured yet (phase={Phase})");
            return $"no Identification captured yet (phase={Phase})";
        }
        if (string.IsNullOrEmpty(capturedPlayerUid))
        {
            Log.Warn($"[s{Id}] disconnect rejected: no PlayerUID parsed from Identification (sticky route impossible)");
            return "no PlayerUID parsed from Identification";
        }
        if (stickies == null)
        {
            Log.Warn($"[s{Id}] disconnect rejected: no sticky route table available on this session");
            return "sticky route table unavailable";
        }
        if (string.IsNullOrEmpty(target.Host)) return "disconnect target has empty host";
        if (target.Port <= 0 || target.Port > 65535) return $"disconnect target has invalid port {target.Port}";

        var ttl = stickyTtl ?? TimeSpan.FromMinutes(5);
        Log.Info($"[s{Id}] DISCONNECT-TRANSFER -> {target} (phase {Phase}, ttl {ttl.TotalSeconds:F0}s, reason='{reason ?? "<none>"}')");

        // Pre-mint reservation on the target backend (skip if no registry/ServerId).
        if (registry != null && !string.IsNullOrEmpty(target.ServerId))
        {
            using var mintCts = CancellationTokenSource.CreateLinkedTokenSource(sessionStopToken);
            mintCts.CancelAfter(TimeSpan.FromSeconds(10));
            var reservation = await registry.MintReservationAsync(
                capturedPlayerUid!, capturedPlayerName ?? "", target.ServerId,
                reason ?? "proxy disconnect-transfer", mintCts.Token,
                ClientEndpoint.ip, ClientEndpoint.port).ConfigureAwait(false);
            if (reservation == null)
            {
                if (failOnRegistryError) { Log.Warn($"[s{Id}] disconnect-transfer aborted: registry mint failed for uid={capturedPlayerUid} target={target.ServerId}"); return "registry mint failed"; }
                Log.Warn($"[s{Id}] proceeding without reservation: registry mint failed");
            }
            else
            {
                Log.Info($"[s{Id}] reservation minted id={reservation.Id} target={reservation.TargetServerId} ttl={reservation.ExpiresAtUnix - DateTimeOffset.UtcNow.ToUnixTimeSeconds()}s");
            }
        }
        else if (registry == null || string.IsNullOrEmpty(target.ServerId))
        {
            Log.Trace($"[s{Id}] disconnect-transfer: skipping reservation mint (registry={(registry != null ? "set" : "null")}, ServerId='{target.ServerId}')");
        }

        // Stage the sticky route before sending the disconnect, so a reconnect that races the
        // disconnect packet still finds the entry waiting.
        stickies.Stage(capturedPlayerUid!, target, ttl, reason ?? "");

        // Build a friendly disconnect message. The client renders this verbatim on the
        // disconnect screen above the Reconnect button.
        string targetLabel = !string.IsNullOrEmpty(target.ServerId)
            ? target.ServerId
            : $"{target.Host}:{target.Port}";
        string clientMsg = string.IsNullOrEmpty(reason)
            ? $"Transferring you to {targetLabel}.\n\nClick \"Reconnect\" to continue."
            : $"Transferring you to {targetLabel}: {reason}\n\nClick \"Reconnect\" to continue.";

        byte[] frame;
        try { frame = DisconnectBuilder.BuildDisconnectFrame(clientMsg); }
        catch (Exception ex) { Log.Warn($"[s{Id}] disconnect frame build failed: {ex.Message}"); return $"frame build failed: {ex.Message}"; }

        try
        {
            await clientStream.WriteAsync(frame, sessionStopToken).ConfigureAwait(false);
            await clientStream.FlushAsync(sessionStopToken).ConfigureAwait(false);
            Log.Info($"[s{Id}] disconnect frame sent to client ({frame.Length}B), sticky route staged for uid={capturedPlayerUid}");
        }
        catch (Exception ex)
        {
            Log.Warn($"[s{Id}] disconnect write to client failed: {ex.Message}");
            return $"client write failed: {ex.Message}";
        }

        // Brief drain so the client processes the packet, then tear down. HandleDisconnectPlayer
        // runs synchronously on the packet dispatch tick, 250ms is well over what's needed.
        try { await Task.Delay(250, sessionStopToken).ConfigureAwait(false); } catch { }
        Close();
        Log.Info($"[s{Id}] AUDIT op=disconnect target={target} reason='{reason ?? ""}' uid={capturedPlayerUid ?? ""} ttl_s={ttl.TotalSeconds:F0} result=ok duration_ms={sw.ElapsedMilliseconds}");
        return null;
    }

    private static async Task SafeAwait(Task t) { try { await t.ConfigureAwait(false); } catch { } }
}
