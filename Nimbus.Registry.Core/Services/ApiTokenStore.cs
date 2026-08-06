using System.Collections.Concurrent;
using Nimbus.Shared.Models;

namespace Nimbus.Registry.Services;

// The issued scoped credentials (#54), keyed on the hash of the secret because that is the only
// form of it that exists here: validating a presented token is one SHA-256 and one dictionary
// lookup, never a scan.
//
// Given a state file the list survives a restart, the same way the ban list and the whitelist do
// since #83. It has to: a bot credential that dies with the registry process is unusable, and a
// revocation that dies with it is worse, because the token the operator revoked yesterday would
// authenticate again this morning.
//
// Nothing is ever dropped from this store. An expired token and a revoked one both stop
// authenticating and both stay in the file, because the record is the audit trail: it says who
// held what and when it was last used, which is exactly the question asked after a leak.
public sealed class ApiTokenStore
{
    // At most one file write per token per minute for a last-used stamp. The stamp is telemetry,
    // the file write is not free, and a bot polling every second would otherwise rewrite the
    // whole file every second to record something nobody reads at that resolution.
    public const int LastUsedWriteIntervalSeconds = 60;

    private readonly ConcurrentDictionary<string, ApiToken> _byHash = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _lastUsePersistedAt = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock;
    private readonly RegistryStateFile<ApiToken>? _state;

    public ApiTokenStore(TimeProvider? clock = null, RegistryStateFile<ApiToken>? state = null)
    {
        _clock = clock ?? TimeProvider.System;
        _state = state;
        if (_state is null) return;

        long now = _clock.GetUtcNow().ToUnixTimeSeconds();
        bool dropped = false;
        foreach (var token in _state.Load())
        {
            // A record with no hash authenticates nobody and cannot be revoked by anyone. It is
            // the one shape worth dropping, and dropping it is what rewrites the file.
            if (string.IsNullOrEmpty(token.Hash) || string.IsNullOrEmpty(token.Id)) { dropped = true; continue; }
            _byHash[token.Hash] = token;
            _lastUsePersistedAt[token.Id] = token.LastUsedAtUnix;
        }
        // Written back only when the file and the list disagree, so a registry restarting in a
        // loop does not churn the file.
        if (dropped) Persist(now);
    }

    public ApiToken Add(ApiToken token)
    {
        _byHash[token.Hash] = token;
        _lastUsePersistedAt[token.Id] = token.LastUsedAtUnix;
        Persist();
        return token;
    }

    // The lookup the auth path runs. Case is fixed by ApiTokenSecret.Hash on both sides, so an
    // ordinal comparison is the whole match.
    public ApiToken? FindByHash(string hash)
    {
        if (string.IsNullOrEmpty(hash)) return null;
        return _byHash.TryGetValue(hash, out var token) ? token : null;
    }

    public ApiToken? FindById(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        return _byHash.Values.FirstOrDefault(token => string.Equals(token.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    // False when no token carries that id, or when it was already revoked: a second revoke is
    // not a failure worth reporting differently, but it is not a change either, so it does not
    // rewrite the file.
    public bool Revoke(string id)
    {
        var token = FindById(id);
        if (token is null || token.Revoked) return false;
        token.Revoked = true;
        // Write-through, or the revocation lives only in memory and the credential comes back at
        // the next restart. That is the failure this whole store exists to prevent.
        Persist();
        return true;
    }

    // Stamps the use and writes it through at most once a minute per token. The in-memory value
    // always moves, so a listing taken right after a call is accurate; only the file lags.
    public void RecordUse(ApiToken token, long nowUnix)
    {
        token.LastUsedAtUnix = nowUnix;
        long persisted = _lastUsePersistedAt.GetValueOrDefault(token.Id);
        if (nowUnix - persisted < LastUsedWriteIntervalSeconds) return;
        _lastUsePersistedAt[token.Id] = nowUnix;
        Persist(nowUnix);
    }

    // Every token ever issued, expired and revoked included. An operator asking why a bot stopped
    // working needs to see the token that expired, not an absence.
    public List<ApiToken> All()
    {
        var list = new List<ApiToken>(_byHash.Count);
        foreach (var kv in _byHash) list.Add(kv.Value);
        list.Sort(static (a, b) => a.CreatedAtUnix.CompareTo(b.CreatedAtUnix));
        return list;
    }

    public int Count => _byHash.Count;

    private void Persist() => Persist(_clock.GetUtcNow().ToUnixTimeSeconds());

    private void Persist(long nowUnix) => _state?.Save(_byHash.Values, nowUnix);
}
