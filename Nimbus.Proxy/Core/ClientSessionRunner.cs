using System.Net.Sockets;

namespace Nimbus.Proxy;

internal sealed class ClientSessionRunner
{
    private readonly BackendRouter router;
    private readonly EventBus events;
    private readonly ServerStatusResponder statusResponder;
    private readonly StickyRouteTable stickies;
    private readonly ProxyConfig cfg;
    private readonly CancellationToken stopToken;

    public ClientSessionRunner(BackendRouter router, EventBus events, ServerStatusResponder statusResponder,
        StickyRouteTable stickies, ProxyConfig cfg, CancellationToken stopToken)
    {
        this.router = router;
        this.events = events;
        this.statusResponder = statusResponder;
        this.stickies = stickies;
        this.cfg = cfg;
        this.stopToken = stopToken;
    }

    public async Task RunAsync(ProxySession session, TcpClient client)
    {
        try
        {
            var firstFrame = await TryReadFirstFrameAsync(client).ConfigureAwait(false);
            if (firstFrame.Length > 0 && await statusResponder.TryHandleAsync(client, firstFrame).ConfigureAwait(false))
                return;

            var connectEvt = new PlayerConnectEvent(session);
            await events.FireAsync(connectEvt).ConfigureAwait(false);
            if (connectEvt.IsDenied)
            {
                Log.Info($"[s{session.Id}] connection denied by handler: {connectEvt.DenyReason}");
                await TryForgeDisconnectAsync(client, connectEvt.DenyReason ?? "connection refused").ConfigureAwait(false);
                try { client.Close(); } catch { }
                return;
            }

            IReadOnlyList<BackendEndpoint> ordered = Array.Empty<BackendEndpoint>();
            string? selectReason = null;
            if (TryConsumeStickyRoute(session, firstFrame, out var stickyTarget))
            {
                ordered = new[] { stickyTarget };
            }
            else
            {
                using var selectCts = CancellationTokenSource.CreateLinkedTokenSource(stopToken);
                selectCts.CancelAfter(TimeSpan.FromSeconds(5));
                (ordered, selectReason) = await router.SelectOrderedAsync(selectCts.Token).ConfigureAwait(false);
            }

            var firstChoice = ordered.Count == 0 ? null : ordered[0].ToServerInfo();
            var chooseEvt = new PlayerChooseInitialServerEvent(session, firstChoice);
            await events.FireAsync(chooseEvt).ConfigureAwait(false);
            if (chooseEvt.IsCancelled)
            {
                Log.Info($"[s{session.Id}] initial connect cancelled by handler: {chooseEvt.CancelReason}");
                await TryForgeDisconnectAsync(client, chooseEvt.CancelReason ?? "connection cancelled").ConfigureAwait(false);
                try { client.Close(); } catch { }
                return;
            }

            if (chooseEvt.Target is ServerInfo overrideSi &&
                (firstChoice == null
                 || !string.Equals(overrideSi.Host, firstChoice.Host, StringComparison.OrdinalIgnoreCase)
                 || overrideSi.Port != firstChoice.Port
                 || !string.Equals(overrideSi.ServerId, firstChoice.ServerId, StringComparison.OrdinalIgnoreCase)))
            {
                ordered = new[] { overrideSi.ToEndpoint() };
            }

            if (ordered.Count == 0)
            {
                Log.Warn($"[s{session.Id}] no healthy backend: {selectReason}; sending forged disconnect");
                await TryForgeDisconnectAsync(client, $"No backend available right now ({selectReason}). Please try again shortly.").ConfigureAwait(false);
                try { client.Close(); } catch { }
                return;
            }

            await session.RunAsync(ordered, firstFrame).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn($"[s{session.Id}] session crashed: {ex.GetType().Name}: {ex.Message}");
            try { client.Close(); } catch { }
        }
    }

    // Resolve the backend a redirect transfer staged for this reconnect, before any upstream is
    // opened. Routing has to be decided here: the mp token the client is about to present is
    // single use, so the backend that answers its token query is the only one that can validate
    // it, and moving the session afterwards costs the player a kick.
    //
    // A client that sends Identification first is matched on its UID. A stock client is not: its
    // first frame is LoginTokenQuery, which carries no identity, and a client slow enough to say
    // nothing inside the first-frame window gives us no frame at all. Both fall back to the
    // client address the redirect path staged alongside the UID.
    private bool TryConsumeStickyRoute(ProxySession session, ReadOnlySpan<byte> firstFrame, out BackendEndpoint target)
    {
        target = default!;

        if (firstFrame.Length > 0 && IdentificationParser.TryExtract(firstFrame, out var uid, out _))
        {
            if (!stickies.TryConsume(uid, out var known)) return false;
            target = known.Target;
            Log.Trace($"[s{session.Id}] sticky route by uid {uid} -> {known.Target} ({known.Reason})");
            return true;
        }

        string ip = session.ClientRemote;
        if (!stickies.TryConsumeByClientIp(ip, out var route)) return false;
        target = route.Target;
        // The player behind this route is a guess until Identification turns up. The session
        // checks it then and puts the route back if we handed it to the wrong person.
        session.NoteRoutedByStickyIp(route);
        Log.Info($"[s{session.Id}] sticky route by client ip {ip} -> {route.Target} ({route.Reason}), expecting uid {route.Uid}");
        return true;
    }

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

    private async Task<byte[]> TryReadFirstFrameAsync(TcpClient client)
    {
        var stream = client.GetStream();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stopToken);
        cts.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(100, cfg.Status.QueryTimeoutMs)));

        var header = new byte[4];
        if (!await ReadExactAsync(stream, header, cts.Token).ConfigureAwait(false))
            return Array.Empty<byte>();

        int rawLen = (header[0] << 24) | (header[1] << 16) | (header[2] << 8) | header[3];
        int len = rawLen & 0x7FFFFFFF;
        if (len == 0)
            return header;
        if (len > 256 * 1024 * 1024)
            throw new InvalidDataException($"client frame too large: {len} bytes");

        var frame = new byte[4 + len];
        Buffer.BlockCopy(header, 0, frame, 0, 4);
        if (!await ReadExactAsync(stream, frame.AsMemory(4, len), cts.Token).ConfigureAwait(false))
            return Array.Empty<byte>();

        return frame;
    }

    private static async Task<bool> ReadExactAsync(NetworkStream stream, Memory<byte> buffer, CancellationToken ct)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read;
            try { read = await stream.ReadAsync(buffer.Slice(offset), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return false; }
            catch (IOException) { return false; }
            catch (ObjectDisposedException) { return false; }
            if (read <= 0) return false;
            offset += read;
        }
        return true;
    }
}
