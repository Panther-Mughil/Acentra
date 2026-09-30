using Acentra.Domain.Abstractions;
using Acentra.Infrastructure.ControlPlane.Configurations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Acentra.Infrastructure.ControlPlane;

/// <summary>
/// Control-plane lookup of tenants by slug. Deliberately <em>uncached</em>: this is an
/// authorization input, so a newly created tenant must resolve on the very next request and a
/// suspended tenant must stop resolving on the very next request. The lookup is a single query on
/// a unique, indexed column, so correctness at the security boundary wins over a micro-optimisation.
///
/// Only <see cref="TenantStatus.Active"/> tenants are ever resolvable; a suspended tenant fails
/// closed (treated as unknown by the caller) and the precise cause is logged here.
/// </summary>
public sealed class TenantRegistry(
    ControlPlaneDbContext db,
    ILogger<TenantRegistry> logger) : ITenantRegistry
{
    public async Task<TenantDescriptor?> FindBySlugAsync(string slug, CancellationToken ct)
    {
        var normalized = TenantSlug.TryNormalize(slug);
        if (normalized is null)
        {
            return null;
        }

        // One query either way (and it carries Status), so distinguishing "unknown" from
        // "suspended" in the log costs no extra work and cannot skew response timing.
        var row = await db.Tenants
            .AsNoTracking()
            .Where(t => t.Slug == normalized)
            .Select(t => new { t.Id, t.Slug, t.Name, t.DatabaseName, t.Status })
            .FirstOrDefaultAsync(ct);

        if (row is null)
        {
            logger.LogWarning("Tenant lookup miss: no tenant with slug '{Slug}'.", normalized);
            return null;
        }

        if (!string.Equals(row.Status, TenantStatus.Active, StringComparison.Ordinal))
        {
            logger.LogWarning(
                "Tenant '{Slug}' is not active (status '{Status}'); treated as unresolvable.",
                normalized, row.Status);
            return null;
        }

        return new TenantDescriptor(row.Id, row.Slug, row.Name, row.DatabaseName);
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
