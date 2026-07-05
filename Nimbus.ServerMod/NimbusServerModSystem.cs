namespace Nimbus.ServerMod;

using System.Collections.Concurrent;
using System.Text;
using Nimbus.Shared.Models;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

public sealed class NimbusServerModSystem : ModSystem
{
    private const string ConfigFileName = "nimbus-server.json";

    private ICoreServerAPI? api;
    private IServerNetworkChannel? channel;
    private NimbusServerConfig config = new();
    private NimbusRegistryClient? registry;
    private CancellationTokenSource? stop;
    private Task? heartbeatTask;
    private long startUnix;

    private readonly ConcurrentDictionary<string, NimbusClientCapability> capabilities = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, PendingSeamlessHandshake> pendingSeamless = new(StringComparer.Ordinal);
    // Forwarding data keyed by playerUID. Populated when a player arrives via the proxy
    // (reservation consumed on join). Cleared on disconnect.
    private readonly ConcurrentDictionary<string, TransferReservation> forwarding = new(StringComparer.OrdinalIgnoreCase);

    public NetworkSnapshot LastSnapshot { get; private set; } = new();
    public DateTime LastSnapshotUtc { get; private set; }
    public string LastStatus { get; private set; } = "not started";

    public override bool ShouldLoad(EnumAppSide side)
        => side == EnumAppSide.Server;

    public override void StartServerSide(ICoreServerAPI api)
    {
        this.api = api;
        config = LoadConfig(api);
        RegisterNetwork(api);
        RegisterCommands(api);

        api.Event.PlayerDisconnect += player =>
        {
            capabilities.TryRemove(player.PlayerUID, out _);
            forwarding.TryRemove(player.PlayerUID, out _);
        };
        api.Event.PlayerJoin += OnPlayerJoin;

        if (!config.Enabled)
        {
            LastStatus = "disabled";
            api.Logger.Notification("Nimbus server mod loaded but disabled");
            return;
        }

        if (!IsConfigured(config))
        {
            LastStatus = "misconfigured";
            api.Logger.Warning("Nimbus server mod is enabled but server id, registry url, public host, or shared secret is missing");
            return;
        }

        registry = new NimbusRegistryClient(config);
        stop = new CancellationTokenSource();
        startUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        heartbeatTask = Task.Run(() => HeartbeatLoopAsync(stop.Token));
        LastStatus = "starting";
        api.Logger.Notification($"Nimbus server mod started as {config.ServerId}");
    }

    public override void Dispose()
    {
        try { stop?.Cancel(); } catch { }
        try { heartbeatTask?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        registry?.Dispose();
        stop?.Dispose();
    }

    public bool HasSeamlessCapability(IServerPlayer player)
        => capabilities.TryGetValue(player.PlayerUID, out var cap) && cap.SupportsSeamlessTransfers;

    public async Task<TransferIntentResponse> RequestTransferAsync(IServerPlayer player, string targetServerId, string? reason, string requestedBy)
    {
        if (!config.Enabled || registry == null)
            return new TransferIntentResponse { Ok = false, Error = "Nimbus server mod is disabled" };

        var req = new TransferIntentRequest
        {
            PlayerUid = player.PlayerUID,
            PlayerName = player.PlayerName,
            SourceServerId = config.ServerId,
            TargetServerId = targetServerId,
            Mode = config.TransferMode,
            Reason = reason,
            RequestedBy = requestedBy,
            TtlSeconds = 30,
            ClientSupportsSeamlessTransfers = HasSeamlessCapability(player)
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(config.RegistryHttpTimeoutSeconds + 1));
        return await registry.PostTransferIntentAsync(req, cts.Token).ConfigureAwait(false)
            ?? new TransferIntentResponse { Ok = false, Error = "no response from registry" };
    }

    private static bool IsConfigured(NimbusServerConfig cfg)
        => !string.IsNullOrWhiteSpace(cfg.ServerId)
        && !string.IsNullOrWhiteSpace(cfg.RegistryUrl)
        && !string.IsNullOrWhiteSpace(cfg.PublicHost)
        && !string.IsNullOrWhiteSpace(cfg.SharedSecret);

    private static NimbusServerConfig LoadConfig(ICoreServerAPI api)
    {
        NimbusServerConfig? cfg = null;
        try { cfg = api.LoadModConfig<NimbusServerConfig>(ConfigFileName); }
        catch (Exception ex) { api.Logger.Warning($"Could not load {ConfigFileName}: {ex.Message}"); }

        cfg ??= new NimbusServerConfig();
        cfg.Normalize();
        api.StoreModConfig(cfg, ConfigFileName);
        return cfg;
    }

    private void RegisterNetwork(ICoreServerAPI api)
    {
        channel = api.Network.RegisterChannel(NimbusModProtocol.ChannelName)
            .RegisterMessageType<NimbusClientHello>()
            .RegisterMessageType<NimbusSeamlessPrepare>()
            .RegisterMessageType<NimbusSeamlessCommit>()
            .RegisterMessageType<NimbusSeamlessReady>()
            .RegisterMessageType<NimbusSeamlessAbort>()
            .SetMessageHandler<NimbusClientHello>(OnClientHello)
            .SetMessageHandler<NimbusSeamlessReady>(OnSeamlessReady);
    }

    private void OnClientHello(IServerPlayer player, NimbusClientHello hello)
    {
        capabilities[player.PlayerUID] = new NimbusClientCapability(hello.ProtocolVersion, hello.SupportsSeamlessTransfers);
    }

    private void OnSeamlessReady(IServerPlayer player, NimbusSeamlessReady ready)
    {
        if (string.IsNullOrWhiteSpace(ready.TransferId))
            return;

        if (pendingSeamless.TryGetValue(ready.TransferId, out var pending) &&
            string.Equals(pending.PlayerUid, player.PlayerUID, StringComparison.OrdinalIgnoreCase))
        {
            pending.Ready.TrySetResult(true);
            api?.Logger.Notification($"Nimbus seamless ready ack from {player.PlayerName} ({ready.TransferId})");
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        if (registry == null || api == null) return;

        int interval = Math.Max(1, config.HeartbeatIntervalSeconds);
        int failures = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var response = await registry.HeartbeatAsync(BuildHeartbeat(), ct).ConfigureAwait(false);
                if (response?.Ok == true)
                {
                    LastStatus = "ok";
                    failures = 0;
                    if (response.NextHeartbeatSeconds > 0) interval = response.NextHeartbeatSeconds;
                }
                else
                {
                    failures++;
                    LastStatus = "heartbeat rejected: " + (response?.Message ?? "no response");
                }

                var snapshot = await registry.GetServersAsync(ct).ConfigureAwait(false);
                if (snapshot != null)
                {
                    LastSnapshot = snapshot;
                    LastSnapshotUtc = DateTime.UtcNow;
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                failures++;
                LastStatus = "error: " + ex.Message;
                if (failures == 1 || failures % 12 == 0)
                    api.Logger.Warning($"Nimbus heartbeat failed ({failures}x): {ex.Message}");
            }

            try { await Task.Delay(TimeSpan.FromSeconds(interval), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private BackendHeartbeat BuildHeartbeat()
    {
        int players = api?.Server.Players.Count(p => p.ConnectionState != EnumClientState.Offline) ?? 0;
        int maxPlayers = api?.Server.Config.MaxClients ?? 0;

        return new BackendHeartbeat
        {
            ServerId = config.ServerId,
            DisplayName = string.IsNullOrWhiteSpace(config.DisplayName) ? config.ServerId : config.DisplayName,
            PublicHost = config.PublicHost,
            PublicPort = config.PublicPort,
            Tags = config.Tags.ToArray(),
            Players = players,
            MaxPlayers = maxPlayers,
            Tps = 0,
            UptimeSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - startUnix,
            Maintenance = config.Maintenance,
            ReservationRequired = config.ReservationRequired,
            GameVersion = GameVersion.OverallVersion,
            RequiredClientMods = BuildRequiredClientMods()
        };
    }

    private BackendModInfo[] BuildRequiredClientMods()
    {
        if (api == null) return Array.Empty<BackendModInfo>();

        var result = new List<BackendModInfo>();
        foreach (var mod in api.ModLoader.Mods)
        {
            var info = mod.Info;
            if (info == null || !info.RequiredOnClient) continue;
            if (info.Side != EnumAppSide.Universal) continue;
            result.Add(new BackendModInfo { Id = info.ModID ?? "", Version = info.Version ?? "" });
        }
        return result.ToArray();
    }

    private TextCommandResult ReloadConfigCommand()
    {
        if (api == null) return TextCommandResult.Error("Not initialized.");
        try
        {
            var fresh = api.LoadModConfig<NimbusServerConfig>(ConfigFileName) ?? new NimbusServerConfig();
            fresh.Normalize();
            config = fresh;
            api.StoreModConfig(config, ConfigFileName);
            return TextCommandResult.Success($"Nimbus config reloaded. {config.StatusSummary()}");
        }
        catch (Exception ex)
        {
            return TextCommandResult.Error($"Nimbus config reload failed: {ex.Message}");
        }
    }

    private void OnPlayerJoin(IServerPlayer player)
    {
        // Always run when registry is wired — stores forwarding data even when gating is off.
        if (registry == null) return;
        _ = Task.Run(() => CheckForwardingAsync(player));
    }

    private async Task CheckForwardingAsync(IServerPlayer player)
    {
        // Capture before await — player object may be partially cleaned up by the time
        // the network call returns if VS processes a voluntary disconnect in the meantime.
        string playerName = player.PlayerName;
        string playerUid = player.PlayerUID;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(config.RegistryHttpTimeoutSeconds + 1));
            var reservation = await registry!.ConsumeReservationByUidAsync(playerUid, config.ServerId, cts.Token)
                .ConfigureAwait(false);

            if (reservation != null)
            {
                // Store the forwarded identity so other systems can trust the real IP.
                forwarding[playerUid] = reservation;
                var realIp = string.IsNullOrEmpty(reservation.RealRemoteIp) ? "?" : reservation.RealRemoteIp;
                api?.Logger.Notification($"[Nimbus] {playerName} forwarded from {realIp}");
            }
            else if (config.ReservationRequired)
            {
                const string reason = "Direct connections are not permitted. Please connect via the Nimbus proxy.";
                api?.Logger.Notification($"[Nimbus] {playerName} blocked — no valid proxy reservation");
                // This continuation runs on a thread-pool thread (Task.Run + ConfigureAwait(false));
                // the server API is not safe to call off the game thread, so hand the kick back.
                api?.Event.EnqueueMainThreadTask(() =>
                {
                    try { player.SendMessage(0, reason, EnumChatType.Notification); } catch { }
                    try { player.Disconnect(reason); } catch { }
                }, "nimbus-reservation-kick");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            api?.Logger.Warning($"[Nimbus] Forwarding check failed for {playerName}: {ex.Message}");
        }
    }

    // Returns Nimbus forwarding data for a player who joined via the proxy.
    // Null if the player connected directly or the registry is not configured.
    public TransferReservation? GetForwardedPlayer(string playerUid)
        => forwarding.TryGetValue(playerUid, out var r) ? r : null;

    private void RegisterCommands(ICoreServerAPI api)
    {
        var parsers = api.ChatCommands.Parsers;
        api.ChatCommands.GetOrCreate("nimbus")
            .WithDescription("Nimbus backend commands")
            .RequiresPrivilege(Privilege.controlserver)
            .BeginSubCommand("status")
                .HandleWith(_ => StatusCommand())
            .EndSubCommand()
            .BeginSubCommand("servers")
                .HandleWith(_ => ServersCommand())
            .EndSubCommand()
            .BeginSubCommand("reload")
                .HandleWith(_ => ReloadConfigCommand())
            .EndSubCommand()
            .BeginSubCommand("send")
                .WithArgs(parsers.Word("player"), parsers.Word("serverId"), parsers.OptionalAll("reason"))
                .HandleWith(SendCommand)
            .EndSubCommand();

        api.ChatCommands.GetOrCreate("server")
            .WithDescription("Move yourself to another Nimbus backend")
            .RequiresPlayer()
            .WithArgs(parsers.Word("serverId"))
            .HandleWith(SelfServerCommand);

        api.ChatCommands.GetOrCreate("join")
            .WithDescription("Join a Nimbus backend by name")
            .RequiresPrivilege(Privilege.chat)
            .RequiresPlayer()
            .WithArgs(parsers.Word("serverId"))
            .HandleWith(SelfServerCommand);
    }

    private TextCommandResult StatusCommand()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Nimbus");
        sb.AppendLine("Mode: " + config.StatusSummary());
        sb.AppendLine("Last status: " + LastStatus);
        if (LastSnapshotUtc != default)
        {
            var age = (int)(DateTime.UtcNow - LastSnapshotUtc).TotalSeconds;
            sb.AppendLine($"Snapshot: {LastSnapshot.Backends.Count} backends, {LastSnapshot.TotalPlayers}/{LastSnapshot.TotalCapacity} players, {age}s old");
        }
        return TextCommandResult.Success(sb.ToString());
    }

    private TextCommandResult ServersCommand()
    {
        if (!config.Enabled) return TextCommandResult.Error("Nimbus is disabled.");
        if (LastSnapshot.Backends.Count == 0) return TextCommandResult.Success("No backend snapshot yet.");

        var sb = new StringBuilder();
        foreach (var backend in LastSnapshot.Backends.OrderBy(b => b.ServerId))
        {
            string flags = string.Join(" ", new[]
            {
                backend.Stale ? "stale" : "",
                backend.Maintenance ? "maintenance" : "",
                backend.ReservationRequired ? "reservation" : ""
            }.Where(s => s.Length > 0));
            sb.AppendLine($"{backend.ServerId}: {backend.DisplayName} {backend.PublicHost}:{backend.PublicPort} {backend.Players}/{backend.MaxPlayers} {flags}".TrimEnd());
        }
        return TextCommandResult.Success(sb.ToString());
    }

    private TextCommandResult SendCommand(TextCommandCallingArgs args)
    {
        string playerName = args[0] as string ?? "";
        string targetServerId = args[1] as string ?? "";
        string? reason = args.Parsers[2].IsMissing ? null : args[2] as string;

        var player = api?.Server.Players.FirstOrDefault(p => string.Equals(p.PlayerName, playerName, StringComparison.OrdinalIgnoreCase));
        if (player == null) return TextCommandResult.Error($"Player '{playerName}' is not online.");
        return BeginTransfer(player, targetServerId, reason, "admin:" + (args.Caller?.GetName() ?? "console"));
    }

    private TextCommandResult SelfServerCommand(TextCommandCallingArgs args)
    {
        if (!config.AllowPlayerServerCommand) return TextCommandResult.Error("Player server switching is disabled.");
        if (args.Caller.Player is not IServerPlayer player) return TextCommandResult.Error("Run this command in-game.");
        return BeginTransfer(player, args[0] as string ?? "", null, "player:" + player.PlayerUID);
    }

    // Validates eagerly then fires the async handshake off the game tick thread so .Wait()
    // never blocks the thread that also needs to dispatch incoming ready-ack packets.
    private TextCommandResult BeginTransfer(IServerPlayer player, string targetServerId, string? reason, string requestedBy)
    {
        if (!config.Enabled) return TextCommandResult.Error("Nimbus is disabled.");
        if (string.Equals(targetServerId, config.ServerId, StringComparison.OrdinalIgnoreCase))
            return TextCommandResult.Error($"You are already on '{targetServerId}'.");

        var target = LastSnapshot.Backends.FirstOrDefault(b => string.Equals(b.ServerId, targetServerId, StringComparison.OrdinalIgnoreCase));
        if (target == null) return TextCommandResult.Error($"Target '{targetServerId}' is not in the registry snapshot.");
        if (target.Stale) return TextCommandResult.Error($"Target '{targetServerId}' is stale.");
        if (target.Maintenance) return TextCommandResult.Error($"Target '{targetServerId}' is in maintenance.");

        _ = Task.Run(() => RunTransferAsync(player, target, reason, requestedBy));
        return TextCommandResult.Success($"Transferring {player.PlayerName} to {target.DisplayName}...");
    }

    private async Task RunTransferAsync(IServerPlayer player, BackendSnapshot target, string? reason, string requestedBy)
    {
        bool seamlessMode = string.Equals(config.TransferMode, "seamless", StringComparison.OrdinalIgnoreCase)
                         || string.Equals(config.TransferMode, "splice", StringComparison.OrdinalIgnoreCase);
        string? transferId = null;

        try
        {
            if (seamlessMode)
            {
                if (channel == null)
                {
                    api?.Logger.Warning("Nimbus seamless handshake failed: channel is null");
                    return;
                }

                transferId = NewTransferId();
                var pending = new PendingSeamlessHandshake(player.PlayerUID);
                pendingSeamless[transferId] = pending;

                try
                {
                    channel.SendPacket(new NimbusSeamlessPrepare
                    {
                        TransferId = transferId,
                        TargetServerId = target.ServerId,
                        Reason = reason ?? "server transfer"
                    }, player);

                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(config.SeamlessPrepareAckTimeoutSeconds));
                    try
                    {
                        await pending.Ready.Task.WaitAsync(cts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        TrySendSeamlessAbort(player, transferId, "client did not acknowledge seamless prepare in time");
                        api?.Logger.Warning($"Nimbus seamless timed out waiting for ready ack from {player.PlayerName}");
                        return;
                    }
                }
                finally
                {
                    pendingSeamless.TryRemove(transferId, out _);
                }
            }

            Task<TransferIntentResponse> task = string.IsNullOrWhiteSpace(transferId)
                ? RequestTransferAsync(player, target.ServerId, reason, requestedBy)
                : RequestTransferWithClientTransferIdAsync(player, target.ServerId, reason, requestedBy, transferId);

            var response = await task.ConfigureAwait(false);
            if (!response.Ok)
            {
                if (!string.IsNullOrWhiteSpace(transferId))
                    TrySendSeamlessAbort(player, transferId, "registry rejected seamless transfer");
                api?.Logger.Warning($"Nimbus transfer for {player.PlayerName} rejected: {response.Error}");
                return;
            }

            api?.Logger.Notification($"Nimbus transfer queued: {player.PlayerName} -> {target.ServerId}");
        }
        catch (Exception ex)
        {
            api?.Logger.Warning($"Nimbus transfer async error: {ex.GetType().Name}: {ex.Message}");
            if (!string.IsNullOrWhiteSpace(transferId))
                TrySendSeamlessAbort(player, transferId, "internal error");
        }
    }

    private async Task<TransferIntentResponse> RequestTransferWithClientTransferIdAsync(IServerPlayer player,
        string targetServerId, string? reason, string requestedBy, string transferId)
    {
        if (!config.Enabled || registry == null)
            return new TransferIntentResponse { Ok = false, Error = "Nimbus server mod is disabled" };

        var req = new TransferIntentRequest
        {
            PlayerUid = player.PlayerUID,
            PlayerName = player.PlayerName,
            SourceServerId = config.ServerId,
            TargetServerId = targetServerId,
            Mode = config.TransferMode,
            Reason = reason,
            RequestedBy = requestedBy,
            TtlSeconds = 30,
            ClientTransferId = transferId,
            ClientSupportsSeamlessTransfers = HasSeamlessCapability(player)
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(config.RegistryHttpTimeoutSeconds + 1));
        return await registry.PostTransferIntentAsync(req, cts.Token).ConfigureAwait(false)
            ?? new TransferIntentResponse { Ok = false, Error = "no response from registry" };
    }

    private static string NewTransferId()
    {
        Span<byte> bytes = stackalloc byte[10];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes);
    }

    private void TrySendSeamlessCommit(IServerPlayer player, string transferId)
    {
        try { channel?.SendPacket(new NimbusSeamlessCommit { TransferId = transferId }, player); }
        catch (Exception ex) { api?.Logger.Warning($"Nimbus seamless commit send failed: {ex.Message}"); }
    }

    private void TrySendSeamlessAbort(IServerPlayer player, string transferId, string message)
    {
        try
        {
            channel?.SendPacket(new NimbusSeamlessAbort { TransferId = transferId, Message = message }, player);
            api?.Logger.Warning($"Nimbus: seamless transfer aborted for {player.PlayerName}: {message}");
        }
        catch (Exception ex) { api?.Logger.Warning($"Nimbus: seamless abort send failed: {ex.Message}"); }
    }

    private sealed record NimbusClientCapability(int ProtocolVersion, bool SupportsSeamlessTransfers);
    private sealed record PendingSeamlessHandshake(string PlayerUid)
    {
        public TaskCompletionSource<bool> Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
