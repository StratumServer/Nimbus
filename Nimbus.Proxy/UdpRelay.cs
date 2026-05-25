using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace Nimbus.Proxy;

// UDP relay running alongside the TCP listener on the same port.
//
// VS uses UDP for position updates. The server pairs a UDP datagram with a TCP client by
// remembering the (srcIp, srcPort) of the first UDP packet (it carries a LoginToken). The
// proxy has to give the backend a stable, unique source endpoint per real client. If multiple
// clients shared one outbound UDP socket, the backend's endpoint->client map would break.
//
// One inbound UDP socket on (ListenHost, ListenPort), one dedicated outbound UdpClient per
// unique client source endpoint. Replies from the backend arrive on the dedicated outbound
// socket so we always know which client to forward them to.
internal sealed class UdpRelay
{
    private readonly ProxyConfig cfg;
    private readonly CancellationToken stopToken;
    private readonly UdpRouteOverrides overrides;
    private readonly ConcurrentDictionary<IPEndPoint, ClientUdpSession> sessions = new();
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(2);

    public UdpRelay(ProxyConfig cfg, CancellationToken stopToken, UdpRouteOverrides overrides)
    {
        this.cfg = cfg;
        this.stopToken = stopToken;
        this.overrides = overrides;
    }

    public async Task RunAsync()
    {
        var bindAddr = IPAddress.Parse(cfg.ListenHost == "0.0.0.0" ? "0.0.0.0" : cfg.ListenHost);
        UdpClient inbound;
        try
        {
            inbound = new UdpClient(new IPEndPoint(bindAddr, cfg.ListenPort));
        }
        catch (Exception ex)
        {
            Log.Warn($"udp: could not bind {bindAddr}:{cfg.ListenPort} ({ex.Message}). UDP relay disabled; clients will TCP-fallback for positions.");
            return;
        }
        Log.Info($"udp listening on {bindAddr}:{cfg.ListenPort} -> backend {cfg.DefaultBackend}");

        _ = Task.Run(IdleSweepAsync, stopToken);

        try
        {
            while (!stopToken.IsCancellationRequested)
            {
                UdpReceiveResult res;
                try { res = await inbound.ReceiveAsync(stopToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                catch (SocketException ex) { Log.Warn($"udp inbound recv error: {ex.SocketErrorCode}"); continue; }

                var src = res.RemoteEndPoint;
                var sess = sessions.GetOrAdd(src, ep => CreateSession(ep, inbound, ResolveTarget(ep)));

                // If a TCP swap retargeted this client IP since the UDP session opened, rebind
                // the upstream socket to the new backend so UDP follows the player.
                var currentTarget = ResolveTarget(src);
                if (!EndpointEquals(sess.Target, currentTarget))
                {
                    Log.Info($"udp rebind for {src}: {sess.Target} -> {currentTarget}");
                    sess.Dispose();
                    sessions.TryRemove(src, out _);
                    sess = sessions.GetOrAdd(src, ep => CreateSession(ep, inbound, currentTarget));
                }

                sess.LastActivityUtc = DateTime.UtcNow;
                try
                {
                    await sess.Upstream.SendAsync(res.Buffer, res.Buffer.Length).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log.Warn($"udp c->s send failed for {src}: {ex.Message}");
                }
            }
        }
        finally
        {
            try { inbound.Dispose(); } catch { }
            foreach (var s in sessions.Values) s.Dispose();
            sessions.Clear();
            Log.Info("udp relay stopped");
        }
    }

    private BackendEndpoint ResolveTarget(IPEndPoint clientSrc)
    {
        if (overrides.TryGet(clientSrc.Address, out var t)) return t;
        return cfg.DefaultBackend;
    }

    private static bool EndpointEquals(BackendEndpoint a, BackendEndpoint b)
        => string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase) && a.Port == b.Port;

    private ClientUdpSession CreateSession(IPEndPoint clientSrc, UdpClient inbound, BackendEndpoint target)
    {
        // Bind to ephemeral local port. The backend sees this socket's local endpoint as the
        // client's UDP source, stable for the life of the session.
        var upstream = new UdpClient(0);
        try { upstream.Connect(target.Host, target.Port); }
        catch (Exception ex) { Log.Warn($"udp upstream connect failed for {clientSrc}: {ex.Message}"); }

        var sess = new ClientUdpSession(clientSrc, upstream, target);
        Log.Info($"udp session opened for {clientSrc} via local {upstream.Client.LocalEndPoint} -> {cfg.DefaultBackend}");

        // Pump replies backend->client.
        _ = Task.Run(async () =>
        {
            try
            {
                while (!stopToken.IsCancellationRequested)
                {
                    UdpReceiveResult res;
                    try { res = await upstream.ReceiveAsync(stopToken).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                    catch (ObjectDisposedException) { return; }
                    catch (SocketException) { return; }

                    sess.LastActivityUtc = DateTime.UtcNow;
                    try { await inbound.SendAsync(res.Buffer, res.Buffer.Length, clientSrc).ConfigureAwait(false); }
                    catch (Exception ex) { Log.Warn($"udp s->c send failed for {clientSrc}: {ex.Message}"); }
                }
            }
            catch { }
        }, stopToken);

        return sess;
    }

    private async Task IdleSweepAsync()
    {
        while (!stopToken.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(30), stopToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }

            var now = DateTime.UtcNow;
            foreach (var kv in sessions)
            {
                if (now - kv.Value.LastActivityUtc > IdleTimeout)
                {
                    if (sessions.TryRemove(kv.Key, out var dead))
                    {
                        dead.Dispose();
                        Log.Info($"udp session idle-closed for {kv.Key}");
                    }
                }
            }
        }
    }

    private sealed class ClientUdpSession : IDisposable
    {
        public IPEndPoint Client { get; }
        public UdpClient Upstream { get; }
        public BackendEndpoint Target { get; }
        public DateTime LastActivityUtc;

        public ClientUdpSession(IPEndPoint client, UdpClient upstream, BackendEndpoint target)
        {
            Client = client;
            Upstream = upstream;
            Target = target;
            LastActivityUtc = DateTime.UtcNow;
        }

        public void Dispose() { try { Upstream.Dispose(); } catch { } }
    }
}
