namespace Acentra.MultiTenant.Core.Entities;

public interface IMultiTenant
{
    Guid TenantId { get; set; }
}
