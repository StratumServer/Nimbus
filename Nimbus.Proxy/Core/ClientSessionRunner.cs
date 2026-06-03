using System.Net.Sockets;

namespace Nimbus.Proxy;

internal sealed class ClientSessionRunner
{
    private readonly BackendRouter router;
    private readonly EventBus events;
    private readonly CancellationToken stopToken;

    public ClientSessionRunner(BackendRouter router, EventBus events, CancellationToken stopToken)
    {
        this.router = router;
        this.events = events;
        this.stopToken = stopToken;
    }

    public async Task RunAsync(ProxySession session, TcpClient client)
    {
        try
        {
            var connectEvt = new PlayerConnectEvent(session);
            await events.FireAsync(connectEvt).ConfigureAwait(false);
            if (connectEvt.IsDenied)
            {
                Log.Info($"[s{session.Id}] connection denied by handler: {connectEvt.DenyReason}");
                await TryForgeDisconnectAsync(client, connectEvt.DenyReason ?? "connection refused").ConfigureAwait(false);
                try { client.Close(); } catch { }
                return;
            }

            IReadOnlyList<BackendEndpoint> ordered;
            string? selectReason;
            using (var selectCts = CancellationTokenSource.CreateLinkedTokenSource(stopToken))
            {
                selectCts.CancelAfter(TimeSpan.FromSeconds(5));
                (ordered, selectReason) = await router.SelectOrderedAsync(selectCts.Token).ConfigureAwait(false);
            }

            var firstChoice = ordered.Count == 0 ? null : ServerInfo.From(ordered[0]);
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

            await session.RunAsync(ordered).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn($"[s{session.Id}] session crashed: {ex.GetType().Name}: {ex.Message}");
        }
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
}
