using System;
using Acentra.MultiTenant.Core.Entities;

namespace Acentra.MultiTenant.Core.Interfaces;

public interface ITenantService
{
    Guid? CurrentTenantId { get; }
    string? CurrentTenantCode { get; }
    Tenant? CurrentTenant { get; }
    
    void SetTenant(Tenant tenant);
    void SetTenantId(Guid tenantId, string? code = null);
}
