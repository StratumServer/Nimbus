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
                var sess = GetSession(src, res.Buffer, inbound);
                if (sess == null) continue;

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

    private ClientUdpSession? GetSession(IPEndPoint src, byte[] packet, UdpClient inbound)
    {
        if (sessions.TryGetValue(src, out var session) && session.IsDisposed)
        {
            sessions.TryRemove(src, out _);
            session = null;
        }
        if (session == null)
        {
            var authorization = authorizations.Claim(src, packet);
            return authorization == null ? null : OpenSession(src, inbound, authorization);
        }
        if (session.Authorization.Revoked.IsCancellationRequested) { session.Dispose(); return null; }
        if (EndpointEquals(session.Target, session.Authorization.Target)) return session;
        session.Dispose();
        sessions.TryRemove(src, out _);
        return OpenSession(src, inbound, session.Authorization);
    }

    private ClientUdpSession? OpenSession(IPEndPoint src, UdpClient inbound,
        UdpAuthorizationTable.Authorization authorization)
    {
        if (authorization.Revoked.IsCancellationRequested) return null;
        var lease = admission.TryAcquire(src.Address);
        if (lease == null) return null;
        try
        {
            var session = CreateSession(src, inbound, authorization.Target, authorization, lease);
            sessions[src] = session;
            return session;
        }
        catch (Exception ex) { Log.Warn($"udp session connect failed: {ex.Message}"); return null; }
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

        _ = Task.Run(() => PumpRepliesAsync(sess, inbound, sessionToken), CancellationToken.None);

        return sess;
    }

    private async Task PumpRepliesAsync(ClientUdpSession session, UdpClient inbound, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                UdpReceiveResult res;
                try { res = await session.Upstream.ReceiveAsync(token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                catch (ObjectDisposedException) { return; }
                catch (SocketException) { return; }
                session.LastActivityUtc = DateTime.UtcNow;
                try { await inbound.SendAsync(res.Buffer, res.Buffer.Length, session.Client).ConfigureAwait(false); }
                catch (Exception ex) { Log.Warn($"udp s->c send failed for {session.Client}: {ex.Message}"); }
            }
        }
        finally { session.Dispose(); }
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
