using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace Nimbus.Proxy;

// VS ties UDP traffic to the source endpoint that first sends its LoginToken.
// Each client gets a dedicated outbound UDP socket so the backend can keep that mapping.
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
        var listenEp = cfg.ListenEndPoint();
        UdpClient inbound;
        try
        {
            inbound = new UdpClient(listenEp);
        }
        catch (Exception ex)
        {
            Log.Warn($"udp: could not bind {listenEp} ({ex.Message}). UDP relay disabled; clients will TCP-fallback for positions.");
            return;
        }
        var defaultBackend = cfg.DefaultBackend();
        Log.Info($"udp listening on {listenEp} -> backend {defaultBackend}");

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
        return cfg.DefaultBackend();
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
                        dead.Dispose();
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
