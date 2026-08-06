namespace Nimbus.Shared;

// The values that mean "nobody has chosen a secret here yet". Every one of them is printed in this
// repository, in the wiki or on a panel's variable screen, so a component that finds one in its
// config has been handed something anyone can read rather than a credential (#40).
//
// One list rather than a literal per component: the components disagreeing about what counts as a
// placeholder is exactly how a value the proxy refuses to serve on ends up accepted by a backend.
// Nothing is ever removed from it either, because an operator upgrading from an older release
// still has whatever literal that release shipped sitting in their config file.
public static class SecretPlaceholders
{
    // What the panel eggs put in the NIMBUS_SHARED_SECRET variable. A sentence rather than a
    // plausible secret on purpose: the panel shows it to the admin during install, and the install
    // scripts refuse to finish while it is still there.
    public const string Egg = "REPLACE-ME-THE-INSTALL-REFUSES-THIS-VALUE";

    // What Nimbus.ServerMod writes into a nimbus-server.json it had to create. The backend
    // consumes the secret the proxy or the registry generated and cannot generate its own: two
    // independently generated values never match, so the file names where to copy one from.
    public const string BackendConfig = "PASTE-THE-SECRET-FROM-nimbus.proxy.toml";

    private static readonly string[] Known =
    {
        "change-me-and-keep-secret",            // shipped as the default everywhere through 0.4.0
        "REPLACE_ME_WITH_A_LONG_RANDOM_STRING", // the wiki's older wording for the same thing
        Egg,
        BackendConfig,
    };

    // Blank counts. A config key nobody filled in and a config key filled in with the documented
    // literal leave the same network open, and telling them apart helps nobody.
    public static bool IsPlaceholder(string? secret)
        => string.IsNullOrWhiteSpace(secret) || Array.IndexOf(Known, secret) >= 0;
}
