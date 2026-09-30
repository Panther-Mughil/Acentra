using Acentra.Domain.Abstractions;
using Acentra.Domain.Entities;
using Acentra.Infrastructure.TenantData.Configurations;
using Microsoft.EntityFrameworkCore;

namespace Acentra.Infrastructure.TenantData;

/// <summary>
/// The per-tenant inventory context. Physical isolation comes from the connection (one PostgreSQL
/// database per tenant, routed by <see cref="TenantDbContextFactory"/>); the global query filters
/// below are the deliberate *second* layer, so a connection-routing bug on its own still cannot
/// leak another tenant's rows.
///
/// <para>
/// The tenant is taken as a constructor parameter and held in a field — never read from an ambient
/// service at query time — and the filter is written against that instance field. EF caches the
/// compiled model, but a filter that references a context-instance member becomes a query parameter
/// bound to the executing context, so one cached model stays correct for every instance. This is
/// asserted empirically by <c>TenantDataIsolationTests</c> (a query against tenant A's database while
/// acting as tenant B must return B's rows only, after A's model is already cached) rather than
/// trusted from this comment.
/// </para>
///
/// <para>
/// This type must NEVER be registered in the DI container, never be a singleton and never be held
/// across interactions: the Blazor circuit's tenant is mutable, so a cached context (or its
/// connection) would serve the previous tenant's data. Always create it per unit of work through
/// <see cref="ITenantDbContextFactory"/>.
/// </para>
/// </summary>
public sealed class AppDbContext : DbContext
{
    private readonly ITenantContext _tenant;

    public AppDbContext(DbContextOptions<AppDbContext> options, ITenantContext tenant)
        : base(options)
    {
        ArgumentNullException.ThrowIfNull(tenant);

        _tenant = tenant;
    }

    public DbSet<Product> Products => Set<Product>();

    public DbSet<StockLevel> StockLevels => Set<StockLevel>();

    public DbSet<StockMovement> StockMovements => Set<StockMovement>();

    public DbSet<TenantFile> Files => Set<TenantFile>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        // Configurations are applied explicitly rather than discovered assembly-wide.
        // ControlPlaneDbContext calls ApplyConfigurationsFromAssembly for the whole
        // Acentra.Infrastructure assembly, so an IEntityTypeConfiguration implementation in
        // TenantData/Configurations would be pulled into the *control-plane* model as well and make
        // it report pending changes at startup. See the note on each configuration type.
        ProductConfiguration.Configure(builder.Entity<Product>());
        StockLevelConfiguration.Configure(builder.Entity<StockLevel>());
        StockMovementConfiguration.Configure(builder.Entity<StockMovement>());
        TenantFileConfiguration.Configure(builder.Entity<TenantFile>());

        // One filter per tenant-owned entity: nothing reachable through this context is unfiltered.
        // Every statement (including Include/navigation loads) carries `WHERE "TenantId" = @tenant`.
        // The single parameter is this instance's field — see the type remarks.
        builder.Entity<Product>().HasQueryFilter(p => p.TenantId == _tenant.TenantId);
        builder.Entity<StockLevel>().HasQueryFilter(s => s.TenantId == _tenant.TenantId);
        builder.Entity<StockMovement>().HasQueryFilter(m => m.TenantId == _tenant.TenantId);
        builder.Entity<TenantFile>().HasQueryFilter(f => f.TenantId == _tenant.TenantId);
    }
}
