using DocProc.Application.Security;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace DocProc.Infrastructure.Security;

/// <summary>
/// Hash lookup in platform scope (the tenant is not known until the key is), then the checks that matter: not
/// revoked, tenant active. Touches LastUsedAt at most every few minutes so the hot path stays a single read.
/// </summary>
public sealed class ApiKeyValidator(ISystemDbContextFactory contexts, TimeProvider clock) : IApiKeyValidator
{
    public static readonly TimeSpan LastUsedResolution = TimeSpan.FromMinutes(5);

    public async Task<ApiKeyIdentity?> ValidateAsync(string presentedKey, CancellationToken cancellationToken)
    {
        if (!ApiKeys.IsWellFormed(presentedKey))
        {
            return null;
        }

        var hash = ApiKeys.Hash(presentedKey);
        await using var db = contexts.CreatePlatformScoped();

        var match = await db.ApiKeys
            .Where(k => k.Hash == hash)
            .Select(k => new { Key = k, TenantStatus = k.Tenant!.Status })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (match is null || match.Key.RevokedAt is not null || match.TenantStatus != TenantStatus.Active)
        {
            return null;
        }

        var now = clock.GetUtcNow();
        if (match.Key.LastUsedAt is null || now - match.Key.LastUsedAt.Value >= LastUsedResolution)
        {
            match.Key.LastUsedAt = now;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return new ApiKeyIdentity(
            match.Key.Id,
            match.Key.TenantId,
            match.Key.Name,
            match.Key.Prefix,
            match.Key.Scopes.Select(s => s.ToString().ToLowerInvariant()).ToList());
    }
}
