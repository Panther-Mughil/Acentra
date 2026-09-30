using Acentra.Domain.Abstractions;

namespace Acentra.Infrastructure.TenantData;

public interface ITenantProvisioner
{
    Task ProvisionAsync(TenantDescriptor tenant, CancellationToken ct);
}
