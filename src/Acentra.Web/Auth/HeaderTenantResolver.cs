using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace Acentra.Web.Auth;

/// <summary>Default hint: the <c>X-Tenant</c> header. Highest precedence.</summary>
public sealed class HeaderTenantResolver : ITenantResolver
{
    public string? Resolve(HttpContext context)
    {
        if (context.Request.Headers.TryGetValue(TenantResolutionConstants.HeaderName, out StringValues values))
        {
            var slug = values.ToString().Trim();

            if (!string.IsNullOrEmpty(slug))
            {
                return slug;
            }
        }

        return null;
    }
}
