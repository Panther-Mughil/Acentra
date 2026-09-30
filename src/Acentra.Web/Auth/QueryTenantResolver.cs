using Microsoft.AspNetCore.Http;

namespace Acentra.Web.Auth;

/// <summary>
/// Query-string hint (<c>?tenant=acme</c>). Lets the UI persist a switch with a plain
/// navigation (no JavaScript) and makes a switch HTTP-testable. Precedence: below the header.
/// </summary>
public sealed class QueryTenantResolver : ITenantResolver
{
    public TenantHint Resolve(HttpContext context)
    {
        if (context.Request.Query.TryGetValue(TenantResolutionConstants.QueryKey, out var values))
        {
            var slug = values.ToString().Trim();

            if (!string.IsNullOrEmpty(slug))
            {
                return new TenantHint(slug, TenantHintSource.Query);
            }
        }

        return new TenantHint(null, TenantHintSource.None);
    }
}
