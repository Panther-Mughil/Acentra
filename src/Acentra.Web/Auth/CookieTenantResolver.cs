using Microsoft.AspNetCore.Http;

namespace Acentra.Web.Auth;

/// <summary>
/// Lowest-precedence hint: the continuity cookie written by
/// <c>TenantResolutionMiddleware</c>. Its only job is to carry a previously authorized
/// selection into the SignalR handshake (and across a reload) where no header is available.
/// It is a hint like any other and is re-authorized against membership on every request.
/// </summary>
public sealed class CookieTenantResolver : ITenantResolver
{
    public TenantHint Resolve(HttpContext context)
    {
        if (context.Request.Cookies.TryGetValue(TenantResolutionConstants.CookieName, out var value))
        {
            var slug = value?.Trim();

            if (!string.IsNullOrEmpty(slug))
            {
                return new TenantHint(slug, TenantHintSource.Cookie);
            }
        }

        return new TenantHint(null, TenantHintSource.None);
    }
}
