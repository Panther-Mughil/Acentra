using Acentra.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Acentra.Infrastructure.ControlPlane.Configurations;

/// <summary>
/// Maps <see cref="TenantMembership"/>. Membership is the authority for tenant access –
/// the client-supplied slug is only ever a hint.
/// </summary>
public sealed class TenantMembershipConfiguration : IEntityTypeConfiguration<TenantMembership>
{
    public void Configure(EntityTypeBuilder<TenantMembership> builder)
    {
        builder.ToTable("TenantMemberships");
        builder.HasKey(m => m.Id);

        builder.Property(m => m.TenantId).IsRequired();
        builder.Property(m => m.UserId).IsRequired();

        builder.Property(m => m.Role)
            .IsRequired()
            .HasMaxLength(16)
            .HasConversion<string>();

        builder.HasIndex(m => new { m.TenantId, m.UserId })
            .IsUnique()
            .HasDatabaseName("ux_tenant_memberships_tenant_user");

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(m => m.TenantId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<AppUser>()
            .WithMany()
            .HasForeignKey(m => m.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
