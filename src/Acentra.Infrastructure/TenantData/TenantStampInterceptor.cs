using Acentra.Domain.Abstractions;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Acentra.Infrastructure.TenantData;

public sealed class TenantStampInterceptor(ITenantContext tenant) : SaveChangesInterceptor
{
    private readonly ITenantContext _tenant = tenant;
}
