using System;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Acentra.MultiTenant.Core.Entities;
using Acentra.MultiTenant.Core.Interfaces;

namespace Acentra.MultiTenant.Infrastructure.Data;

public class AppDbContext : DbContext
{
    private readonly ITenantService? _tenantService;

    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<InventoryItem> InventoryItems => Set<InventoryItem>();
    public DbSet<StockTransaction> StockTransactions => Set<StockTransaction>();
    public DbSet<TenantAuditLog> AuditLogs => Set<TenantAuditLog>();

    public AppDbContext(DbContextOptions<AppDbContext> options, ITenantService? tenantService = null)
        : base(options)
    {
        _tenantService = tenantService;
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Configure Tenant
        modelBuilder.Entity<Tenant>(entity =>
        {
            entity.HasKey(t => t.Id);
            entity.HasIndex(t => t.Code).IsUnique();
        });

        // Apply Global Query Filters for all IMultiTenant entities!
        // This ensures tenant isolation at EF Core data-access layer:
        // No queries will ever return rows belonging to other tenants.
        var currentTenantId = _tenantService?.CurrentTenantId ?? Guid.Empty;

        modelBuilder.Entity<InventoryItem>()
            .HasQueryFilter(e => _tenantService == null || _tenantService.CurrentTenantId == null || e.TenantId == _tenantService.CurrentTenantId);

        modelBuilder.Entity<StockTransaction>()
            .HasQueryFilter(e => _tenantService == null || _tenantService.CurrentTenantId == null || e.TenantId == _tenantService.CurrentTenantId);

        modelBuilder.Entity<TenantAuditLog>()
            .HasQueryFilter(e => _tenantService == null || _tenantService.CurrentTenantId == null || e.TenantId == _tenantService.CurrentTenantId);

        // Indexes for performance
        modelBuilder.Entity<InventoryItem>().HasIndex(i => new { i.TenantId, i.SKU });
        modelBuilder.Entity<StockTransaction>().HasIndex(s => new { s.TenantId, s.InventoryItemId });
    }

    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        // Enforce automatic TenantId population and cross-tenant mutation safety
        var currentTenantId = _tenantService?.CurrentTenantId;

        foreach (var entry in ChangeTracker.Entries<IMultiTenant>())
        {
            if (entry.State == EntityState.Added)
            {
                if (currentTenantId.HasValue && currentTenantId.Value != Guid.Empty)
                {
                    entry.Entity.TenantId = currentTenantId.Value;
                }
            }
            else if (entry.State == EntityState.Modified || entry.State == EntityState.Deleted)
            {
                // Verify entity belongs to active tenant
                if (currentTenantId.HasValue && entry.Entity.TenantId != currentTenantId.Value)
                {
                    throw new InvalidOperationException($"Cross-tenant mutation denied! Entity belongs to Tenant '{entry.Entity.TenantId}', but current context is '{currentTenantId}'.");
                }
            }
        }

        return await base.SaveChangesAsync(cancellationToken);
    }
}
