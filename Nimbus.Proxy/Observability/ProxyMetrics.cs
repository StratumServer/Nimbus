using System.Text;

namespace Nimbus.Proxy;

internal static class ProxyMetrics
{
    private static long activeSessions;
    private static long sessionsAccepted;
    private static long backendConnectAttempts;
    private static long backendConnectSuccesses;
    private static long backendConnectFailures;
    private static long bytesClientToServer;
    private static long bytesServerToClient;
    private static long redirectRequests;
    private static long redirectSuccesses;
    private static long redirectFailures;
    private static long seamlessRequests;
    private static long seamlessSuccesses;
    private static long seamlessFailures;
    private static long adminCommands;
    private static long adminDenied;
    private static long registryIntentPollFailures;
    private static long drainedServers;

    // Live session count, for the /status report.
    public static long ActiveSessionCount => Interlocked.Read(ref activeSessions);

    public static void SessionAccepted()
    {
        Interlocked.Increment(ref sessionsAccepted);
        Interlocked.Increment(ref activeSessions);
    }

    public static void SessionClosed()
        => Interlocked.Decrement(ref activeSessions);

    public static void BackendConnectAttempt()
        => Interlocked.Increment(ref backendConnectAttempts);

    public static void BackendConnectSuccess()
        => Interlocked.Increment(ref backendConnectSuccesses);

    public static void BackendConnectFailure()
        => Interlocked.Increment(ref backendConnectFailures);

    public static void AddBytes(long clientToServer, long serverToClient)
    {
        if (clientToServer > 0) Interlocked.Add(ref bytesClientToServer, clientToServer);
        if (serverToClient > 0) Interlocked.Add(ref bytesServerToClient, serverToClient);
    }

    public static void RedirectRequested()
        => Interlocked.Increment(ref redirectRequests);

    public static void RedirectSucceeded()
        => Interlocked.Increment(ref redirectSuccesses);

    public static void RedirectFailed()
        => Interlocked.Increment(ref redirectFailures);

    public static void SeamlessRequested()
        => Interlocked.Increment(ref seamlessRequests);

    public static void SeamlessSucceeded()
        => Interlocked.Increment(ref seamlessSuccesses);

    public static void SeamlessFailed()
        => Interlocked.Increment(ref seamlessFailures);

    public static void AdminCommand(bool denied)
    {
        Interlocked.Increment(ref adminCommands);
        if (denied) Interlocked.Increment(ref adminDenied);
    }

    public static void RegistryIntentPollFailed()
        => Interlocked.Increment(ref registryIntentPollFailures);

    public static void SetDrainedServers(int count)
        => Interlocked.Exchange(ref drainedServers, Math.Max(0, count));

    public static string RenderPrometheus()
    {
        var sb = new StringBuilder();
        Metric(sb, "nimbus_proxy_sessions_active", "gauge", "Active proxied TCP sessions.", Read(activeSessions));
        Metric(sb, "nimbus_proxy_sessions_accepted_total", "counter", "Client sessions accepted by the proxy.", Read(sessionsAccepted));
        Metric(sb, "nimbus_proxy_backend_connect_attempts_total", "counter", "Backend TCP connect attempts.", Read(backendConnectAttempts));
        Metric(sb, "nimbus_proxy_backend_connect_success_total", "counter", "Backend TCP connect successes.", Read(backendConnectSuccesses));
        Metric(sb, "nimbus_proxy_backend_connect_failures_total", "counter", "Backend TCP connect failures.", Read(backendConnectFailures));
        Metric(sb, "nimbus_proxy_bytes_client_to_server_total", "counter", "Bytes pumped from clients to backends.", Read(bytesClientToServer));
        Metric(sb, "nimbus_proxy_bytes_server_to_client_total", "counter", "Bytes pumped from backends to clients.", Read(bytesServerToClient));
        Metric(sb, "nimbus_proxy_redirect_requests_total", "counter", "Redirect transfer requests.", Read(redirectRequests));
        Metric(sb, "nimbus_proxy_redirect_success_total", "counter", "Redirect transfer successes.", Read(redirectSuccesses));
        Metric(sb, "nimbus_proxy_redirect_failures_total", "counter", "Redirect transfer failures.", Read(redirectFailures));
        Metric(sb, "nimbus_proxy_seamless_requests_total", "counter", "Seamless transfer requests.", Read(seamlessRequests));
        Metric(sb, "nimbus_proxy_seamless_success_total", "counter", "Seamless transfer successes.", Read(seamlessSuccesses));
        Metric(sb, "nimbus_proxy_seamless_failures_total", "counter", "Seamless transfer failures.", Read(seamlessFailures));
        Metric(sb, "nimbus_proxy_admin_commands_total", "counter", "Admin commands dispatched.", Read(adminCommands));
        Metric(sb, "nimbus_proxy_admin_denied_total", "counter", "Admin commands denied by permissions.", Read(adminDenied));
        Metric(sb, "nimbus_proxy_registry_intent_poll_failures_total", "counter", "Transfer intent poll failures.", Read(registryIntentPollFailures));
        Metric(sb, "nimbus_proxy_drained_servers", "gauge", "Servers currently marked drained.", Read(drainedServers));
        return sb.ToString();
    }

    private static long Read(long value)
        => Interlocked.Read(ref value);

    private static void Metric(StringBuilder sb, string name, string type, string help, long value)
    {
        sb.Append("# HELP ").Append(name).Append(' ').Append(help).Append('\n');
        sb.Append("# TYPE ").Append(name).Append(' ').Append(type).Append('\n');
        sb.Append(name).Append(' ').Append(value).Append('\n');
    }
}
