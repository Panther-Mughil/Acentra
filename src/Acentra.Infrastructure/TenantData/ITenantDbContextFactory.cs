using Acentra.Domain.Abstractions;

namespace Acentra.Infrastructure.TenantData;

public interface ITenantDbContextFactory
{
    AppDbContext Create();
    AppDbContext CreateForTenant(TenantDescriptor tenant);
}
