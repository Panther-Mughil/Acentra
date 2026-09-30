using Acentra.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Acentra.Infrastructure.ControlPlane.Configurations;

/// <summary>
/// Maps <see cref="Tenant"/>. The slug is the only public identifier a client may
/// supply, so it is constrained, unique and stored lower-case.
/// </summary>
public sealed class TenantConfiguration : IEntityTypeConfiguration<Tenant>
{
    public const string SlugPattern = "^[a-z0-9-]{2,40}$";

    public void Configure(EntityTypeBuilder<Tenant> builder)
    {
        builder.ToTable("Tenants");
        builder.HasKey(t => t.Id);

        builder.Property(t => t.Slug)
            .IsRequired()
            .HasMaxLength(40);

        builder.HasIndex(t => t.Slug)
            .IsUnique()
            .HasDatabaseName("ux_tenants_slug");

        builder.Property(t => t.Name)
            .IsRequired()
            .HasMaxLength(200);

        builder.Property(t => t.DatabaseName)
            .IsRequired()
            .HasMaxLength(64);

        builder.Property(t => t.Status)
            .IsRequired()
            .HasMaxLength(32)
            .HasDefaultValue(TenantStatus.Active);

        builder.Property(t => t.CreatedUtc)
            .IsRequired();

        // Fail closed: a slug that cannot be normalised to this shape is never resolvable.
        builder.ToTable(t => t.HasCheckConstraint(
            "ck_tenants_slug_format",
            $"\"Slug\" ~ '{SlugPattern}'"));
    }
}

/// <summary>Recognised tenant lifecycle states. Anything else is treated as unavailable.</summary>
public static class TenantStatus
{
    public const string Active = "Active";
    public const string Suspended = "Suspended";
}
