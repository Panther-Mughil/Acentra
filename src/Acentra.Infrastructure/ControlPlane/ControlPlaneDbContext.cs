using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Acentra.Domain.Entities;

namespace Acentra.Infrastructure.ControlPlane;

/// <summary>
/// The control-plane schema: Identity users, tenants and memberships. Its model is built ONLY from
/// the configurations in the <c>Acentra.Infrastructure.ControlPlane.Configurations</c> namespace;
/// discovery is namespace-filtered so an
/// <c>IEntityTypeConfiguration&lt;T&gt;</c> added anywhere else in Acentra.Infrastructure (e.g. the
/// tenant-data tables) can never leak into the control-plane model.
/// </summary>
public class ControlPlaneDbContext(
    DbContextOptions<ControlPlaneDbContext> options)
    : IdentityDbContext<AppUser, IdentityRole<Guid>, Guid>(options)
{
    /// <summary>The only namespace whose configurations belong to this model.</summary>
    internal const string ConfigurationsNamespace = "Acentra.Infrastructure.ControlPlane.Configurations";

    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<TenantMembership> TenantMemberships => Set<TenantMembership>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        // Namespace-filtered on purpose. ApplyConfigurationsFromAssembly without a filter would
        // pull any IEntityTypeConfiguration<T> in the whole assembly into this model; the
        // tenant-data tables did exactly that and broke the startup model validation.
        builder.ApplyConfigurationsFromAssembly(
            typeof(ControlPlaneDbContext).Assembly,
            type => type.Namespace == ConfigurationsNamespace);
    }
}
