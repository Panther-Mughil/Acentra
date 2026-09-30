using Acentra.Domain.Abstractions;
using Acentra.Domain.Entities;
using Acentra.Infrastructure.ControlPlane.Configurations;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Acentra.Infrastructure.ControlPlane;

/// <summary>
/// Development-only control-plane seed. Creates two tenants and one demo user who is a
/// member of *both*, so the UI tenant switcher has something to switch between.
/// Idempotent: safe to run on every dev start. Never called outside Development.
/// </summary>
public static class TenantSeeder
{
    public const string AcmeSlug = "acme";
    public const string GlobexSlug = "globex";
    public const string DemoUserEmail = "demo@acentra.dev";
    public const string DemoUserPassword = "Demo!2345";

    public static async Task SeedAsync(
        ControlPlaneDbContext db,
        UserManager<AppUser> users,
        CancellationToken ct)
    {
        var acme = await EnsureTenantAsync(db, AcmeSlug, "Acme Corporation", "acentra_acme", ct);
        var globex = await EnsureTenantAsync(db, GlobexSlug, "Globex Corporation", "acentra_globex", ct);

        var user = await users.FindByEmailAsync(DemoUserEmail);

        if (user is null)
        {
            user = new AppUser
            {
                UserName = DemoUserEmail,
                Email = DemoUserEmail,
                EmailConfirmed = true
            };

            var result = await users.CreateAsync(user, DemoUserPassword);

            if (!result.Succeeded)
            {
                throw new InvalidOperationException(
                    "Failed to seed demo user: " +
                    string.Join("; ", result.Errors.Select(e => e.Description)));
            }
        }

        await EnsureMembershipAsync(db, acme.Id, user.Id, MembershipRole.Owner, ct);
        await EnsureMembershipAsync(db, globex.Id, user.Id, MembershipRole.Member, ct);
    }

    private static async Task<Tenant> EnsureTenantAsync(
        ControlPlaneDbContext db,
        string slug,
        string name,
        string databaseName,
        CancellationToken ct)
    {
        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Slug == slug, ct);

        if (tenant is not null)
        {
            return tenant;
        }

        tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Slug = slug,
            Name = name,
            DatabaseName = databaseName,
            Status = TenantStatus.Active,
            CreatedUtc = DateTime.UtcNow
        };

        db.Tenants.Add(tenant);
        await db.SaveChangesAsync(ct);
        return tenant;
    }

    private static async Task EnsureMembershipAsync(
        ControlPlaneDbContext db,
        Guid tenantId,
        Guid userId,
        MembershipRole role,
        CancellationToken ct)
    {
        var exists = await db.TenantMemberships
            .AnyAsync(m => m.TenantId == tenantId && m.UserId == userId, ct);

        if (exists)
        {
            return;
        }

        db.TenantMemberships.Add(new TenantMembership
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            UserId = userId,
            Role = role
        });

        await db.SaveChangesAsync(ct);
    }
}
