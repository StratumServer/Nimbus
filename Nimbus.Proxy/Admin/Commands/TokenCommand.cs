using Nimbus.Shared.Models;

namespace Nimbus.Proxy;

// Scoped API tokens (#54), managed the way bans and whitelist entries are: through the admin
// socket, which is the path an operator already has. The proxy reaches the registry's HMAC-only
// /api/tokens endpoints on their behalf, and the plaintext travels back through that chain
// exactly once.
//
// Creating a token is not the same as the registry answering one. `api_tokens_enabled` decides
// whether a bearer header is accepted at all, and it is deliberately unrelated to these commands:
// minting the credential before turning the switch on is the order an operator does it in.
internal sealed class TokenCreateCommand : IAdminCommand
{
    // A token that is shown once and not written down is a token that has to be reissued, so the
    // response says so in a field rather than leaving it to the operator's memory.
    public const string ShownOnceWarning =
        "This is the only time this token will be shown. Store it now; the registry keeps a hash and cannot recover it.";

    public string Name => "token-create";
    public string Permission => "nimbus.command.token.create";
    public string Summary => "mint a scoped API token for a third-party integration";
    public string Usage => "token-create --name <name> --scopes <a,b> [--duration <seconds> | --permanent]";

    public async Task<object> ExecuteAsync(AdminContext ctx)
    {
        if (ctx.Proxy.Registry == null)
            return new { ok = false, reason = "api tokens need a registry (registry.mode is 'disabled')" };

        if (!ctx.Request.TryString("name", out var name))
            return AdminCommandError.Missing(this, "name");

        // Comma-separated on the wire rather than a JSON array, because that is the shape nimctl
        // takes them in and the admin frame is the same string either way.
        var scopes = new List<string>();
        foreach (var part in (ctx.Request.OptionalString("scopes") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var scope = part.Trim();
            if (scope.Length > 0) scopes.Add(scope);
        }
        if (scopes.Count == 0)
            return AdminCommandError.Usage(this, $"need at least one scope; known scopes are {string.Join(", ", Nimbus.Registry.Services.ApiTokenScopes.All)}");

        ctx.Request.TryInt32("duration", out int duration);
        bool permanent = ctx.Request.Bool("permanent");

        var request = new ApiTokenCreateRequest
        {
            Name = name,
            Scopes = scopes,
            DurationSeconds = duration,
            Permanent = permanent,
            CreatedBy = "admin",
        };

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.StopToken);
        cts.CancelAfter(TimeSpan.FromSeconds(ctx.Proxy.RegistryCfg.HttpTimeoutSeconds + 1));
        var created = await ctx.Proxy.Registry.CreateApiTokenAsync(request, cts.Token).ConfigureAwait(false);
        if (created?.Record == null || string.IsNullOrEmpty(created.Token))
            return new { ok = false, reason = "registry refused the token (check the name and the scopes)" };

        return new
        {
            ok = true,
            id = created.Record.Id,
            name = created.Record.Name,
            scopes = created.Record.Scopes,
            createdAtUnix = created.Record.CreatedAtUnix,
            expiresAtUnix = created.Record.ExpiresAtUnix,
            token = created.Token,
            warning = ShownOnceWarning,
        };
    }
}

internal sealed class TokenRevokeCommand : IAdminCommand
{
    public string Name => "token-revoke";
    public string Permission => "nimbus.command.token.revoke";
    public string Summary => "revoke an API token by id";
    public string Usage => "token-revoke --id <id>";

    public async Task<object> ExecuteAsync(AdminContext ctx)
    {
        if (ctx.Proxy.Registry == null)
            return new { ok = false, reason = "api tokens need a registry (registry.mode is 'disabled')" };

        // By id, never by the secret: an operator revoking a leaked token has the listing in front
        // of them, and asking for the secret would mean pasting it somewhere a second time.
        if (!ctx.Request.TryString("id", out var id))
            return AdminCommandError.Missing(this, "id");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.StopToken);
        cts.CancelAfter(TimeSpan.FromSeconds(ctx.Proxy.RegistryCfg.HttpTimeoutSeconds + 1));
        bool revoked = await ctx.Proxy.Registry.RevokeApiTokenAsync(id, cts.Token).ConfigureAwait(false);
        return revoked
            ? new { ok = true, id }
            : new { ok = false, id, reason = "no matching token, or it was already revoked" };
    }
}

internal sealed class TokenListCommand : IAdminCommand
{
    public string Name => "token-list";
    public IReadOnlyList<string> Aliases => new[] { "tokens" };
    public string Permission => "nimbus.command.token.list";
    public string Summary => "list issued API tokens, their scopes and their state";
    public string Usage => "token-list";

    public async Task<object> ExecuteAsync(AdminContext ctx)
    {
        if (ctx.Proxy.Registry == null)
            return new { ok = false, reason = "api tokens need a registry (registry.mode is 'disabled')" };

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.StopToken);
        cts.CancelAfter(TimeSpan.FromSeconds(ctx.Proxy.RegistryCfg.HttpTimeoutSeconds + 1));
        var tokens = await ctx.Proxy.Registry.GetApiTokensAsync(cts.Token).ConfigureAwait(false);
        if (tokens == null)
            return new { ok = false, reason = "registry unreachable" };

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return new
        {
            ok = true,
            count = tokens.Count,
            tokens = tokens.ConvertAll(t => new
            {
                id = t.Id,
                name = t.Name,
                scopes = t.Scopes,
                createdBy = t.CreatedBy,
                createdAtUnix = t.CreatedAtUnix,
                expiresAtUnix = t.ExpiresAtUnix,
                // Zero means never used, which is the reading an operator wants after issuing one.
                lastUsedAtUnix = t.LastUsedAtUnix,
                revoked = t.Revoked,
                // Expired and revoked tokens stay in the listing on purpose: they are the record
                // of what existed, and an absence answers none of the questions asked after a
                // leak. This field is what says which of them still authenticate.
                usable = t.IsUsableAt(now),
            }),
        };
    }
}
