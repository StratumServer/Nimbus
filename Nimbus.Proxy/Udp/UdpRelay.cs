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
    private readonly UdpAuthorizationTable authorizations;
    private readonly UdpAdmission admission;
    private readonly TaskCompletionSource listening = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Task Listening => listening.Task;
    internal int ActiveSessionCount => sessions.Values.Count(session => !session.IsDisposed);
    private readonly ConcurrentDictionary<IPEndPoint, ClientUdpSession> sessions = new();
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(2);

    public UdpRelay(ProxyConfig cfg, CancellationToken stopToken, UdpRouteOverrides overrides,
        UdpAuthorizationTable? authorizations = null)
    {
        this.cfg = cfg;
        this.stopToken = stopToken;
        this.authorizations = authorizations ?? new UdpAuthorizationTable();
        admission = new UdpAdmission(cfg.Advanced);
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
        listening.TrySetResult();
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
                if (res.Buffer.Length > 5000) continue;
                if (sessions.TryGetValue(src, out var sess) && sess.IsDisposed)
                {
                    sessions.TryRemove(src, out _);
                    sess = null;
                }
                if (sess == null)
                {
                    var authorization = authorizations.Claim(src, res.Buffer);
                    if (authorization == null || authorization.Revoked.IsCancellationRequested) continue;
                    var lease = admission.TryAcquire(src.Address);
                    if (lease == null) continue;
                    try { sess = CreateSession(src, inbound, authorization.Target, authorization, lease); }
                    catch (Exception ex) { Log.Warn($"udp session connect failed: {ex.Message}"); continue; }
                    sessions[src] = sess;
                }
                if (sess.Authorization.Revoked.IsCancellationRequested) { sess.Dispose(); continue; }
                var currentTarget = sess.Authorization.Target;
                if (!EndpointEquals(sess.Target, currentTarget))
                {
                    sess.Dispose();
                    sessions.TryRemove(src, out _);
                    var lease = admission.TryAcquire(src.Address);
                    if (lease == null) continue;
                    try { sess = CreateSession(src, inbound, currentTarget, sess.Authorization, lease); }
                    catch (Exception ex) { Log.Warn($"udp session retarget failed: {ex.Message}"); continue; }
                    sessions[src] = sess;
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
            try { inbound.Dispose(); } catch { /* the bind is being torn down anyway */ }
            foreach (var s in sessions.Values) s.Dispose();
            sessions.Clear();
            Log.Info("udp relay stopped");
        }
    }

    private static bool EndpointEquals(BackendEndpoint a, BackendEndpoint b)
        => string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase) && a.Port == b.Port;

    private ClientUdpSession CreateSession(IPEndPoint clientSrc, UdpClient inbound, BackendEndpoint target,
        UdpAuthorizationTable.Authorization authorization, IDisposable lease)
    {
        // Bind to ephemeral local port. The backend sees this socket's local endpoint as the
        // client's UDP source, stable for the life of the session.
        UdpClient upstream;
        try
        {
            upstream = new UdpClient(0);
            try { upstream.Connect(target.Host, target.Port); }
            catch { upstream.Dispose(); throw; }
        }
        catch { lease.Dispose(); throw; }
        var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stopToken, authorization.Revoked);
        var sess = new ClientUdpSession(clientSrc, upstream, target, authorization, lease, lifetime);
        CancellationToken sessionToken = lifetime.Token;

        // Pump replies backend->client.
        _ = Task.Run(async () =>
        {
            try
            {
                while (!sessionToken.IsCancellationRequested)
                {
                    UdpReceiveResult res;
                    try { res = await upstream.ReceiveAsync(sessionToken).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                    catch (ObjectDisposedException) { return; }
                    catch (SocketException) { return; }

                    sess.LastActivityUtc = DateTime.UtcNow;
                    try { await inbound.SendAsync(res.Buffer, res.Buffer.Length, clientSrc).ConfigureAwait(false); }
                    catch (Exception ex) { Log.Warn($"udp s->c send failed for {clientSrc}: {ex.Message}"); }
                }
            }
            catch { /* the three socket exits above are the expected ones; anything else ends this
                       one client's reply pump and nothing more, since the relay keeps serving the
                       others and a new packet from this client rebuilds the session */ }
            finally { sess.Dispose(); }
        });

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
                if ((kv.Value.IsDisposed || now - kv.Value.LastActivityUtc > IdleTimeout) && sessions.TryRemove(kv.Key, out var dead))
                    dead.Dispose();
            }
        }
    }

    private sealed class ClientUdpSession : IDisposable
    {
        public IPEndPoint Client { get; }
        public UdpClient Upstream { get; }
        public BackendEndpoint Target { get; }
        public DateTime LastActivityUtc;
        public UdpAuthorizationTable.Authorization Authorization { get; }
        private readonly IDisposable lease;
        private readonly CancellationTokenSource lifetime;
        private readonly CancellationTokenRegistration registration;
        private int disposed;
        public bool IsDisposed => Volatile.Read(ref disposed) != 0;

        public ClientUdpSession(IPEndPoint client, UdpClient upstream, BackendEndpoint target,
            UdpAuthorizationTable.Authorization authorization, IDisposable lease, CancellationTokenSource lifetime)
        {
            Client = client;
            Upstream = upstream;
            Target = target;
            Authorization = authorization;
            this.lease = lease;
            this.lifetime = lifetime;
            registration = lifetime.Token.Register(upstream.Dispose);
            LastActivityUtc = DateTime.UtcNow;
        }

        // Called from the idle sweep, from a retarget, and from relay shutdown, so a session can
        // be disposed more than once and the socket may already be gone.
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            lifetime.Cancel();
            Upstream.Dispose();
            registration.Dispose();
            lifetime.Dispose();
            lease.Dispose();
        }
    }
}
