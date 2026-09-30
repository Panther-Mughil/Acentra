using Acentra.Domain.Abstractions;
using Acentra.Infrastructure.ControlPlane.Configurations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Acentra.Infrastructure.ControlPlane;

/// <summary>
/// Control-plane lookup of tenants by slug, cached by normalised slug.
/// Only <see cref="TenantStatus.Active"/> tenants are ever resolvable, so a suspended
/// tenant fails closed (treated as unknown) rather than leaking a routing decision.
/// </summary>
public sealed class TenantRegistry(
    ControlPlaneDbContext db,
    IMemoryCache cache) : ITenantRegistry
{
    public static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(2);

    private const string CacheKeyPrefix = "controlplane:tenant:slug:";

    public async Task<TenantDescriptor?> FindBySlugAsync(string slug, CancellationToken ct)
    {
        var normalized = TenantSlug.TryNormalize(slug);
        if (normalized is null)
        {
            return null;
        }

        var descriptor = await cache.GetOrCreateAsync(
            CacheKeyPrefix + normalized,
            async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = CacheTtl;

                var tenant = await db.Tenants
                    .AsNoTracking()
                    .Where(t => t.Slug == normalized && t.Status == TenantStatus.Active)
                    .Select(t => new TenantDescriptor(t.Id, t.Slug, t.Name, t.DatabaseName))
                    .FirstOrDefaultAsync(ct);

                return tenant;
            });

        return descriptor;
    }

    public async Task<IReadOnlyList<TenantDescriptor>> FindForUserAsync(Guid userId, CancellationToken ct)
    {
        if (userId == Guid.Empty)
        {
            return [];
        }

        // Membership is authority, so it is deliberately *not* cached: a revoked
        // membership must take effect on the very next request.
        var query =
            from m in db.TenantMemberships.AsNoTracking()
            join t in db.Tenants.AsNoTracking() on m.TenantId equals t.Id
            where m.UserId == userId && t.Status == TenantStatus.Active
            orderby t.Slug
            select new TenantDescriptor(t.Id, t.Slug, t.Name, t.DatabaseName);

        return await query.ToListAsync(ct);
    }
}
