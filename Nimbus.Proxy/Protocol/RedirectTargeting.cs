namespace Nimbus.Proxy;

// Decides what address a forged redirect packet carries (#18).
//
// Today RedirectFix clients ignore the stamped host: they reconnect to the proxy's
// cached address and the staged sticky route picks the backend. But a vanilla client
// with the redirect crash fixed will dial the stamped host literally, so stamping the
// backend's PublicHost would send it around the proxy. When the operator sets
// transfers.redirect_address to the proxy's own player-facing address, we stamp that
// instead and a literal-following client lands back on the proxy, where the sticky
// route takes over.
internal static class RedirectTargeting
{
    public const int VanillaDefaultPort = 42420;

    public readonly record struct Choice(string HostString, bool ProxyStamped);

    // Returns the "host" or "host:port" string for the redirect frame. Pure so the
    // stamping policy is unit-testable apart from the session plumbing.
    public static Choice Resolve(TransfersConfig transfers, BackendEndpoint target)
    {
        var configured = transfers.RedirectAddress?.Trim();
        if (!string.IsNullOrEmpty(configured))
            return new Choice(configured, ProxyStamped: true);

        // Legacy stamping: the backend's own address, per vanilla VS convention
        // ("host" alone when the port is the default).
        string hostString = (target.Port > 0 && target.Port != VanillaDefaultPort)
            ? $"{target.Host}:{target.Port}"
            : target.Host;
        return new Choice(hostString, ProxyStamped: false);
    }
}
