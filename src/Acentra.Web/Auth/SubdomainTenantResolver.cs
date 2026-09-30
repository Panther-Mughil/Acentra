using Microsoft.AspNetCore.Http;

namespace Acentra.Web.Auth;

/// <summary>
/// Subdomain hint (<c>acme.inventory.app</c>). Only applies when the host has at least
/// three labels, so <c>localhost</c> and <c>acentra.localhost</c> are left alone.
/// </summary>
public sealed class SubdomainTenantResolver : ITenantResolver
{
    public TenantHint Resolve(HttpContext context)
    {
        var host = context.Request.Host.Host;

        if (string.IsNullOrWhiteSpace(host))
        {
            return new TenantHint(null, TenantHintSource.None);
        }

        var labels = host.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (labels.Length < 3)
        {
            return new TenantHint(null, TenantHintSource.None);
        }

        var candidate = labels[0];

        // "www.acme.inventory.app" must not resolve to a tenant called "www".
        if (string.Equals(candidate, "www", StringComparison.OrdinalIgnoreCase))
        {
            return new TenantHint(null, TenantHintSource.None);
        }

        return new TenantHint(candidate, TenantHintSource.Subdomain);
    }
}
