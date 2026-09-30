using Acentra.Domain.Abstractions;

namespace Acentra.Infrastructure.ControlPlane;

public sealed class TenantRegistry : ITenantRegistry
{
    public Task<TenantDescriptor?> FindBySlugAsync(string slug, CancellationToken ct)
    {
        throw new NotImplementedException();
    }

    public Task<IReadOnlyList<TenantDescriptor>> FindForUserAsync(Guid userId, CancellationToken ct)
    {
        throw new NotImplementedException();
    }
}
