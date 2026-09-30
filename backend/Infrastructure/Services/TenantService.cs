using System;
using Acentra.MultiTenant.Core.Entities;
using Acentra.MultiTenant.Core.Interfaces;

namespace Acentra.MultiTenant.Infrastructure.Services;

public class TenantService : ITenantService
{
    public Guid? CurrentTenantId { get; private set; }
    public string? CurrentTenantCode { get; private set; }
    public Tenant? CurrentTenant { get; private set; }

    public void SetTenant(Tenant tenant)
    {
        CurrentTenant = tenant;
        CurrentTenantId = tenant.Id;
        CurrentTenantCode = tenant.Code;
    }

    public void SetTenantId(Guid tenantId, string? code = null)
    {
        CurrentTenantId = tenantId;
        CurrentTenantCode = code ?? tenantId.ToString();
    }
}
