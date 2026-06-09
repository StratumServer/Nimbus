using System.Net;

namespace Nimbus.Proxy;

internal sealed class ProxyConfigValidation
{
    private readonly List<string> errors = new();
    private readonly List<string> warnings = new();

    public IReadOnlyList<string> Errors => errors;
    public IReadOnlyList<string> Warnings => warnings;
    public bool IsValid => errors.Count == 0;

    public void Error(string message) => errors.Add(message);
    public void Warn(string message) => warnings.Add(message);
}

internal static class ProxyConfigValidator
{
    public static ProxyConfigValidation Validate(ProxyConfig cfg)
    {
        var result = new ProxyConfigValidation();

        ValidateEndpoint(cfg.Bind, "bind", requireIpAddress: true, result);
        ValidateServers(cfg, result);
        ValidateTransfers(cfg, result);
        ValidateAdmin(cfg, result);
        ValidateRegistry(cfg, result);
        ValidateMetrics(cfg, result);
        ValidateStatus(cfg, result);
        ValidatePlugins(cfg, result);
        ValidatePersistence(cfg, result);
        ValidateAdvanced(cfg, result);

        return result;
    }

    private static void ValidateServers(ProxyConfig cfg, ProxyConfigValidation result)
    {
        if (cfg.Servers.Count == 0)
        {
            result.Error("[servers] must contain at least one backend");
            return;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in cfg.Servers)
        {
            if (string.IsNullOrWhiteSpace(kv.Key))
            {
                result.Error("[servers] contains an empty server id");
                continue;
            }
            if (!seen.Add(kv.Key))
                result.Error($"[servers] contains duplicate server id '{kv.Key}'");
            ValidateEndpoint(kv.Value, $"servers.{kv.Key}", requireIpAddress: false, result);
        }

        foreach (var serverId in cfg.Try)
        {
            if (string.IsNullOrWhiteSpace(serverId)) continue;
            if (!HasServer(cfg, serverId))
                result.Warn($"try references unknown server '{serverId}'");
        }

        foreach (var serverId in cfg.ProxyProtocolServers)
        {
            if (string.IsNullOrWhiteSpace(serverId)) continue;
            if (!HasServer(cfg, serverId))
                result.Warn($"proxy_protocol_servers references unknown server '{serverId}'");
        }

        foreach (var forced in cfg.ForcedHosts)
        {
            if (string.IsNullOrWhiteSpace(forced.Key))
                result.Warn("[forced-hosts] contains an empty hostname");
            foreach (var serverId in forced.Value)
            {
                if (!HasServer(cfg, serverId))
                    result.Warn($"forced-hosts.{forced.Key} references unknown server '{serverId}'");
            }
        }
    }

    private static void ValidateTransfers(ProxyConfig cfg, ProxyConfigValidation result)
    {
        var mode = NormalizeMode(cfg.Transfers.DefaultMode);
        if (mode is not "redirect" and not "seamless")
            result.Error($"transfers.default_mode must be 'redirect' or 'seamless', got '{cfg.Transfers.DefaultMode}'");
        if (mode == "seamless" && !cfg.Transfers.AllowSeamless)
            result.Error("transfers.default_mode = 'seamless' requires transfers.allow_seamless = true");
        if (mode == "seamless" && cfg.Transfers.RequireSeamlessCapability && !cfg.Transfers.FallbackToRedirectWhenSeamlessUnavailable)
            result.Warn("transfers.default_mode = 'seamless' will reject players without Nimbus client capability instead of falling back to redirect");
        if (cfg.Transfers.AllowSeamless && !cfg.Transfers.RequireSeamlessCapability)
            result.Warn("transfers.require_seamless_capability = false allows seamless requests without the Nimbus client handshake");
        if (cfg.Transfers.EnableUnsafeSeamlessSplice)
            result.Warn("transfers.enable_unsafe_seamless_splice = true allows live splice without Nimbus client capability");
    }

    private static void ValidateAdmin(ProxyConfig cfg, ProxyConfigValidation result)
    {
        if (!cfg.Admin.Enabled) return;

        var ep = ValidateEndpoint(cfg.Admin.Bind, "admin.bind", requireIpAddress: true, result);
        if (ep != null && !IsLoopback(ep.Address) && string.IsNullOrWhiteSpace(cfg.Admin.Secret))
            result.Error("admin.bind is not loopback, so admin.secret must be set");

        if (cfg.Admin.GrantedPermissions.Count == 0)
            result.Warn("admin.granted_permissions is empty; every admin command will be denied");
    }

    private static void ValidateRegistry(ProxyConfig cfg, ProxyConfigValidation result)
    {
        var mode = (cfg.Registry.Mode ?? "").Trim().ToLowerInvariant();
        if (mode is not "" and not "disabled" and not "embedded" and not "remote")
        {
            result.Error($"registry.mode must be 'disabled', 'embedded', or 'remote', got '{cfg.Registry.Mode}'");
            return;
        }

        if (cfg.Registry.ReservationTtlSeconds <= 0)
            result.Error("registry.reservation_ttl_seconds must be greater than zero");
        if (cfg.Registry.MaxReservationTtlSeconds <= 0)
            result.Error("registry.max_reservation_ttl_seconds must be greater than zero");
        if (cfg.Registry.ReservationTtlSeconds > cfg.Registry.MaxReservationTtlSeconds)
            result.Warn("registry.reservation_ttl_seconds is greater than registry.max_reservation_ttl_seconds and will be clamped");
        if (cfg.Registry.TransferIntentPollMs < 250)
            result.Warn("registry.transfer_intent_poll_ms below 250 will be clamped to 250");

        if (mode == "remote")
        {
            if (string.IsNullOrWhiteSpace(cfg.Registry.Url))
                result.Error("registry.url is required when registry.mode = 'remote'");
            else if (!Uri.TryCreate(cfg.Registry.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                result.Error("registry.url must be an absolute http or https URL");
            if (string.IsNullOrWhiteSpace(cfg.Registry.SharedSecret))
                result.Error("registry.shared_secret is required when registry.mode = 'remote'");
        }

        if (mode == "embedded" && !string.IsNullOrWhiteSpace(cfg.Registry.EmbeddedBind))
        {
            if (!Uri.TryCreate(cfg.Registry.EmbeddedBind, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            {
                result.Error("registry.embedded_bind must be an absolute http or https URL, or empty");
            }
            else if (!IsLoopbackOrLocalhost(uri.Host) && IsDefaultSecret(cfg.Registry.EmbeddedSharedSecret))
            {
                result.Error("registry.embedded_bind is not loopback, so registry.embedded_shared_secret must be changed from the default");
            }
        }
    }

    private static void ValidateAdvanced(ProxyConfig cfg, ProxyConfigValidation result)
    {
        if (cfg.Advanced.ConnectTimeoutMs <= 0)
            result.Error("advanced.connect_timeout_ms must be greater than zero");
        if (cfg.Advanced.BufferSize < 1024)
            result.Error("advanced.buffer_size must be at least 1024");
    }

    private static void ValidatePersistence(ProxyConfig cfg, ProxyConfigValidation result)
    {
        if (!cfg.Persistence.PersistDrainFlags) return;
        if (string.IsNullOrWhiteSpace(cfg.Persistence.DrainFlagsFile))
            result.Error("persistence.drain_flags_file must be set when persistence.persist_drain_flags = true");
    }

    private static void ValidateMetrics(ProxyConfig cfg, ProxyConfigValidation result)
    {
        if (!cfg.Metrics.Enabled) return;

        if (!Uri.TryCreate(cfg.Metrics.Bind, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            result.Error("metrics.bind must be an absolute http or https URL");
        else if (!IsLoopbackOrLocalhost(uri.Host))
            result.Warn("metrics.bind is not loopback. Metrics are unauthenticated");
        if (string.IsNullOrWhiteSpace(cfg.Metrics.Path) || !cfg.Metrics.Path.StartsWith('/'))
            result.Error("metrics.path must start with '/'");
    }

    private static void ValidateStatus(ProxyConfig cfg, ProxyConfigValidation result)
    {
        if (!cfg.Status.Enabled) return;
        if (string.IsNullOrWhiteSpace(cfg.Status.Name))
            result.Error("status.name must be set when status.enabled = true");
        if (cfg.Status.MaxPlayers < 0)
            result.Error("status.max_players cannot be negative");
        if (cfg.Status.QueryTimeoutMs < 100)
            result.Error("status.query_timeout_ms must be at least 100");
    }

    private static void ValidatePlugins(ProxyConfig cfg, ProxyConfigValidation result)
    {
        if (!cfg.Plugins.Enabled) return;
        if (string.IsNullOrWhiteSpace(cfg.Plugins.Directory))
            result.Error("plugins.directory must be set when plugins.enabled = true");
        foreach (var id in cfg.Plugins.Disabled)
        {
            if (!IsPluginId(id))
                result.Error($"plugins.disabled contains invalid plugin id '{id}'");
        }
    }

    private static IPEndPoint? ValidateEndpoint(string value, string label, bool requireIpAddress, ProxyConfigValidation result)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            result.Error($"{label}: empty");
            return null;
        }

        int idx = value.LastIndexOf(':');
        if (idx <= 0 || idx == value.Length - 1)
        {
            result.Error($"{label}: must be 'host:port', got '{value}'");
            return null;
        }

        string host = value.Substring(0, idx);
        if (!int.TryParse(value.AsSpan(idx + 1), out int port) || port <= 0 || port > 65535)
        {
            result.Error($"{label}: invalid port in '{value}'");
            return null;
        }

        if (!IPAddress.TryParse(host, out var address))
        {
            if (requireIpAddress)
            {
                result.Error($"{label}: host must be an IP address, got '{host}'");
                return null;
            }
            return null;
        }

        return new IPEndPoint(address, port);
    }

    private static string NormalizeMode(string mode)
        => string.Equals(mode, "splice", StringComparison.OrdinalIgnoreCase) ? "seamless" : (mode ?? "").Trim().ToLowerInvariant();

    private static bool IsLoopback(IPAddress address)
        => IPAddress.IsLoopback(address);

    private static bool IsLoopbackOrLocalhost(string host)
    {
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)) return true;
        return IPAddress.TryParse(host, out var address) && IsLoopback(address);
    }

    private static bool IsDefaultSecret(string secret)
        => secret is "" or "change-me-and-keep-secret" or "REPLACE_ME_WITH_A_LONG_RANDOM_STRING";

    private static bool IsPluginId(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch) || ch is '.' or '_' or '-') continue;
            return false;
        }
        return true;
    }

    private static bool HasServer(ProxyConfig cfg, string serverId)
    {
        foreach (var key in cfg.Servers.Keys)
            if (string.Equals(key, serverId, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }
}
