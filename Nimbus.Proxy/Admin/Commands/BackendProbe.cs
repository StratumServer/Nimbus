using System.Net.Sockets;

namespace Nimbus.Proxy;

// The cheapest question worth asking before a transfer: is anything listening over there. Both
// swap and evacuate ask it, because a redirect closes a working session on its way out and a
// target that will not answer turns that into a kick.
internal static class BackendProbe
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMilliseconds(1000);

    public static async Task<bool> ReachableAsync(string host, int port, TimeSpan timeout)
    {
        using var tcp = new TcpClient { NoDelay = true };
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await tcp.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
            return tcp.Connected;
        }
        catch { return false; }
    }
}
