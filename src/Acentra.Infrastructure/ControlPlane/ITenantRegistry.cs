using Acentra.Domain.Abstractions;

namespace Acentra.Infrastructure.ControlPlane;

public interface ITenantRegistry
{
    Task<TenantDescriptor?> FindBySlugAsync(string slug, CancellationToken ct);
    Task<IReadOnlyList<TenantDescriptor>> FindForUserAsync(Guid userId, CancellationToken ct);
}
