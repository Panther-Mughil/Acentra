using Acentra.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Acentra.Infrastructure.TenantData.Configurations;

/// <summary>
/// Maps <see cref="StockLevel"/>. One row per product per tenant, enforced by a tenant-scoped unique
/// key rather than a global one. Static applier for the reason documented on
/// <see cref="ProductConfiguration"/>.
/// </summary>
public static class StockLevelConfiguration
{
    public static void Configure(EntityTypeBuilder<StockLevel> builder)
    {
        builder.ToTable("StockLevels");
        builder.HasKey(s => s.Id);

        builder.Property(s => s.TenantId)
            .IsRequired();

        builder.HasIndex(s => s.TenantId)
            .HasDatabaseName("ix_stock_levels_tenant_id");

        builder.Property(s => s.ProductId)
            .IsRequired();

        builder.Property(s => s.Quantity)
            .IsRequired();

        builder.Property(s => s.UpdatedUtc)
            .IsRequired();

        builder.HasIndex(s => new { s.TenantId, s.ProductId })
            .IsUnique()
            .HasDatabaseName("ux_stock_levels_tenant_product");
    }
}
