using Acentra.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Acentra.Infrastructure.TenantData.Configurations;

/// <summary>
/// Maps <see cref="Product"/> in a tenant's own database. <c>TenantId</c> stays on every row even
/// though tenants are physically separated: the global query filter is layered on top of the
/// physical split, so both must agree.
///
/// <para>
/// Deliberately a static applier rather than an <c>IEntityTypeConfiguration&lt;Product&gt;</c>:
/// <c>ControlPlaneDbContext</c> calls <c>ApplyConfigurationsFromAssembly</c> for the whole
/// Acentra.Infrastructure assembly, so implementing that interface here would add the tenant tables
/// to the *control-plane* model and make it fail its pending-model-changes validation at startup.
/// </para>
/// </summary>
public static class ProductConfiguration
{
    public static void Configure(EntityTypeBuilder<Product> builder)
    {
        builder.ToTable("Products");
        builder.HasKey(p => p.Id);

        builder.Property(p => p.TenantId)
            .IsRequired();

        builder.HasIndex(p => p.TenantId)
            .HasDatabaseName("ix_products_tenant_id");

        builder.Property(p => p.Sku)
            .IsRequired()
            .HasMaxLength(64);

        builder.Property(p => p.Name)
            .IsRequired()
            .HasMaxLength(200);

        builder.Property(p => p.Description)
            .HasMaxLength(2000);

        builder.Property(p => p.UnitPrice)
            .HasPrecision(18, 2);

        builder.Property(p => p.ReorderLevel)
            .IsRequired();

        builder.Property(p => p.IsActive)
            .IsRequired();

        builder.Property(p => p.CreatedUtc)
            .IsRequired();

        builder.Property(p => p.UpdatedUtc)
            .IsRequired();

        // Tenant-scoped uniqueness: two tenants may both own SKU-001 without colliding.
        builder.HasIndex(p => new { p.TenantId, p.Sku })
            .IsUnique()
            .HasDatabaseName("ux_products_tenant_sku");
    }
}
