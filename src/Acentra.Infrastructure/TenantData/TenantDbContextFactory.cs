using Acentra.Domain.Abstractions;

namespace Acentra.Infrastructure.TenantData;

public sealed class TenantDbContextFactory(ITenantContext tenant) : ITenantDbContextFactory
{
    private readonly ITenantContext _tenant = tenant;

    public AppDbContext Create()
    {
        throw new NotImplementedException();
    }

    public AppDbContext CreateForTenant(TenantDescriptor tenant)
    {
        throw new NotImplementedException();
    }
}
