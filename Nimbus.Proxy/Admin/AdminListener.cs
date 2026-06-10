using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Nimbus.Proxy;

internal sealed class AdminListener
{
    private readonly ProxyConfig cfg;
    private readonly ProxyListener proxy;
    private readonly CancellationToken stopToken;
    private readonly AdminCommandRegistry commands;
    private readonly AdminPermissions permissions;
    private readonly Func<IReadOnlyList<LoadedPlugin>> loadedPlugins;
    private readonly Func<string>? reload;

    public AdminListener(ProxyConfig cfg, ProxyListener proxy, CancellationToken stopToken,
        Func<IReadOnlyList<LoadedPlugin>> loadedPlugins, Func<string>? reload = null)
    {
        this.cfg = cfg;
        this.proxy = proxy;
        this.stopToken = stopToken;
        this.commands = AdminCommandRegistry.Default();
        this.permissions = new AdminPermissions(cfg.Admin.GrantedPermissions);
        this.loadedPlugins = loadedPlugins;
        this.reload = reload;
    }

    public async Task RunAsync()
    {
        if (!cfg.Admin.Enabled)
        {
            Log.Info("admin endpoint disabled (admin.enabled = false)");
            return;
        }
        var adminEp = cfg.Admin.EndPoint();
        var listener = new TcpListener(adminEp);
        listener.Start();
        Log.Info($"admin listening on {adminEp}");

        try
        {
            while (!stopToken.IsCancellationRequested)
            {
                TcpClient c;
                try { c = await listener.AcceptTcpClientAsync(stopToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                _ = Task.Run(() => HandleAsync(c), stopToken);
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        var remote = client.Client.RemoteEndPoint?.ToString() ?? "?";
        Log.Info($"admin client connected from {remote}");
        try
        {
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };

            // Optional auth handshake: when Admin.Secret is set, the FIRST line must be
            // {"cmd":"auth","secret":"..."} with a matching value. Anything else closes the
            // connection without revealing whether auth was required.
            if (!string.IsNullOrEmpty(cfg.Admin.Secret))
            {
                string? first = await reader.ReadLineAsync(stopToken).ConfigureAwait(false);
                if (first == null) return;
                if (!TryAuth(first, cfg.Admin.Secret))
                {
                    await writer.WriteLineAsync(JsonSerializer.Serialize(new { ok = false, reason = "auth required" })).ConfigureAwait(false);
                    Log.Warn($"admin client {remote} failed auth");
                    return;
                }
                await writer.WriteLineAsync(JsonSerializer.Serialize(new { ok = true, authed = true })).ConfigureAwait(false);
            }

            string? line;
            while ((line = await reader.ReadLineAsync(stopToken).ConfigureAwait(false)) != null)
            {
                if (line.Length == 0) continue;
                string response;
                try { response = await DispatchAsync(line).ConfigureAwait(false); }
                catch (Exception ex) { response = JsonSerializer.Serialize(new { ok = false, error = ex.Message }); }
                await writer.WriteLineAsync(response).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
        finally
        {
            try { client.Close(); } catch { }
            Log.Info($"admin client {remote} disconnected");
        }
    }

    // Constant-time comparison of the first message's secret against the configured one.
    private static bool TryAuth(string line, string expectedSecret)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            if (!doc.RootElement.TryGetProperty("cmd", out var cmdEl)) return false;
            if (cmdEl.GetString() != "auth") return false;
            if (!doc.RootElement.TryGetProperty("secret", out var secEl)) return false;
            var supplied = secEl.GetString() ?? "";
            var a = Encoding.UTF8.GetBytes(supplied);
            var b = Encoding.UTF8.GetBytes(expectedSecret);
            return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(a, b);
        }
        catch { return false; }
    }

    private async Task<string> DispatchAsync(string line)
    {
        using var doc = JsonDocument.Parse(line);
        if (!doc.RootElement.TryGetProperty("cmd", out var cmdEl))
            return JsonSerializer.Serialize(new { ok = false, reason = "missing 'cmd'" });
        string cmd = cmdEl.GetString() ?? "";
        if (!commands.TryGet(cmd, out var handler))
            return JsonSerializer.Serialize(new { ok = false, reason = "unknown cmd: " + cmd });
        if (!permissions.Allows(handler.Permission))
        {
            ProxyMetrics.AdminCommand(denied: true);
            return JsonSerializer.Serialize(new { ok = false, reason = "permission denied", permission = handler.Permission });
        }

        var ctx = new AdminContext(proxy, cfg, doc.RootElement, stopToken, permissions, commands.Commands, loadedPlugins(), reload);
        var result = await handler.ExecuteAsync(ctx).ConfigureAwait(false);
        ProxyMetrics.AdminCommand(denied: false);
        return JsonSerializer.Serialize(result);
    }

    internal IReadOnlyCollection<IAdminCommand> Commands => commands.Commands;
}
