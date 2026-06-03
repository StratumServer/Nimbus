using System.Collections.Concurrent;
using Nimbus.Shared.Models;

namespace Nimbus.Proxy;

internal sealed class TransferIntentDispatcher
{
    private readonly ProxyConfig cfg;
    private readonly IRegistryClient registry;
    private readonly ConcurrentDictionary<long, ProxySession> sessions;
    private readonly CancellationToken stopToken;

    public TransferIntentDispatcher(ProxyConfig cfg, IRegistryClient registry,
        ConcurrentDictionary<long, ProxySession> sessions, CancellationToken stopToken)
    {
        this.cfg = cfg;
        this.registry = registry;
        this.sessions = sessions;
        this.stopToken = stopToken;
    }

    public async Task RunAsync()
    {
        var period = TimeSpan.FromMilliseconds(Math.Max(250, cfg.Registry.TransferIntentPollMs));
        while (!stopToken.IsCancellationRequested)
        {
            try { await Task.Delay(period, stopToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }

            try
            {
                var intents = await registry.DrainTransferIntentsAsync(stopToken).ConfigureAwait(false);
                foreach (var intent in intents)
                    _ = Task.Run(() => DispatchAsync(intent), stopToken);
            }
            catch (Exception ex)
            {
                ProxyMetrics.RegistryIntentPollFailed();
                Log.Warn($"transfer-intent poll failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private async Task DispatchAsync(TransferIntent intent)
    {
        if (string.IsNullOrEmpty(intent.PlayerUid) || string.IsNullOrEmpty(intent.TargetServerId)) return;

        ProxySession? match = null;
        foreach (var session in sessions.Values)
        {
            if (string.Equals(session.PlayerUid, intent.PlayerUid, StringComparison.OrdinalIgnoreCase))
            {
                match = session;
                break;
            }
        }

        if (match == null)
        {
            Log.Trace($"intent {intent.Id} for uid={intent.PlayerUid} -> no live session on this proxy, dropping");
            return;
        }

        try
        {
            using var rcts = new CancellationTokenSource(TimeSpan.FromSeconds(cfg.Registry.HttpTimeoutSeconds + 1));
            var backend = await registry.ResolveByServerIdAsync(intent.TargetServerId, rcts.Token).ConfigureAwait(false);
            if (backend == null) { Log.Warn($"intent {intent.Id}: unknown serverId '{intent.TargetServerId}'"); return; }
            if (backend.Stale) { Log.Warn($"intent {intent.Id}: target '{intent.TargetServerId}' is stale"); return; }
            if (backend.Maintenance) { Log.Warn($"intent {intent.Id}: target '{intent.TargetServerId}' is in maintenance"); return; }

            var target = new BackendEndpoint { Host = backend.PublicHost, Port = backend.PublicPort, ServerId = intent.TargetServerId };
            bool seamless = string.Equals(intent.Mode, "seamless", StringComparison.OrdinalIgnoreCase)
                         || string.Equals(intent.Mode, "splice", StringComparison.OrdinalIgnoreCase);

            string? failReason;
            if (seamless && cfg.Transfers.AllowSeamless)
                failReason = await match.RequestSeamlessAsync(target, registry, intent.Reason, cfg.Registry.FailOnError).ConfigureAwait(false);
            else
            {
                if (seamless)
                    Log.Warn($"intent {intent.Id}: seamless requested but disabled in config; falling back to redirect");
                failReason = await match.RequestRedirectAsync(target, registry, intent.Reason, cfg.Registry.FailOnError).ConfigureAwait(false);
            }

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
