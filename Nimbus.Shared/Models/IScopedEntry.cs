namespace Nimbus.Shared.Models;

// The shape the registry's per-scope lists share. A ban and a whitelist entry are the same thing
// read two ways: an entry keyed on a player and a backend scope, that can say whether it is still
// in force and whether it applies to a given backend. RegistryEntryStore and RegistryEntryCache
// are written once against this interface, and NetworkBan and WhitelistEntry keep their own named
// predicates (Blocks, Covers) as the public reading of Matches.
public interface IScopedEntry
{
    string PlayerUid { get; }

    string ServerId { get; }

    // Still in force at this instant. A permanent entry always is, a timed one until it expires.
    bool IsActiveAt(long nowUnix);

    // True when this entry applies to the given backend scope. An empty serverId asks about the
    // network itself. NetworkBan.Blocks and WhitelistEntry.Covers are this same test, named for
    // the side of the gate they sit on.
    bool Matches(string? serverId);
}
