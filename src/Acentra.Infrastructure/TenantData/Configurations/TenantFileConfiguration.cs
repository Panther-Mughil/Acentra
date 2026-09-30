using Acentra.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Acentra.Infrastructure.TenantData.Configurations;

/// <summary>
/// Maps <see cref="TenantFile"/> — the database half of file storage. Only the storage key is
/// persisted; the bytes live behind <c>IFileStorage</c> under a tenant-scoped prefix. Static applier
/// for the reason documented on <see cref="ProductConfiguration"/>.
/// </summary>
public static class TenantFileConfiguration
{
    public static void Configure(EntityTypeBuilder<TenantFile> builder)
    {
        builder.ToTable("TenantFiles");
        builder.HasKey(f => f.Id);

        builder.Property(f => f.TenantId)
            .IsRequired();

        builder.HasIndex(f => f.TenantId)
            .HasDatabaseName("ix_tenant_files_tenant_id");

        // REQ-004 §5: a key is unique per tenant — never globally, so the same key under a different
        // tenant stays legal (and column order keeps the index usable for tenant-only lookups).
        builder.HasIndex(f => new { f.TenantId, f.Key })
            .IsUnique()
            .HasDatabaseName("ux_tenant_files_tenant_key");

        // Supports the per-tenant "most recent uploads" listing without a sort.
        builder.HasIndex(f => new { f.TenantId, f.UploadedUtc })
            .HasDatabaseName("ix_tenant_files_tenant_uploaded");

        builder.Property(f => f.Key)
            .IsRequired()
            .HasMaxLength(512);

        builder.Property(f => f.FileName)
            .IsRequired()
            .HasMaxLength(512);

        builder.Property(f => f.ContentType)
            .IsRequired()
            .HasMaxLength(255);

        builder.Property(f => f.SizeBytes)
            .IsRequired();

        builder.Property(f => f.UploadedUtc)
            .IsRequired();
    }
}
