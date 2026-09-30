using Acentra.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Acentra.Infrastructure.TenantData.Configurations;

/// <summary>
/// Maps <see cref="StockMovement"/> — the append-only audit trail of stock changes. Static applier
/// for the reason documented on <see cref="ProductConfiguration"/>.
/// </summary>
public static class StockMovementConfiguration
{
    public static void Configure(EntityTypeBuilder<StockMovement> builder)
    {
        builder.ToTable("StockMovements");
        builder.HasKey(m => m.Id);

        builder.Property(m => m.TenantId)
            .IsRequired();

        builder.HasIndex(m => m.TenantId)
            .HasDatabaseName("ix_stock_movements_tenant_id");

        builder.Property(m => m.ProductId)
            .IsRequired();

        builder.Property(m => m.Delta)
            .IsRequired();

        builder.Property(m => m.Reason)
            .IsRequired()
            .HasMaxLength(200);

        builder.Property(m => m.Note)
            .HasMaxLength(2000);

        builder.Property(m => m.OccurredUtc)
            .IsRequired();
    }
}
