using Microsoft.AspNetCore.Http;

namespace Acentra.Web.Auth;

/// <summary>
/// Which surface a tenant hint was read from. The middleware needs this because it is the
/// only way to know whether a failed hint may clear the continuity cookie: a cookie-sourced
/// failure is a stale selection to discard, whereas a header/query/subdomain failure must
/// leave the cookie alone (otherwise a crafted link could wipe a valid selection).
/// </summary>
public enum TenantHintSource
{
    /// <summary>No hint was supplied by any resolver.</summary>
    None,

    /// <summary>The <c>X-Tenant</c> header.</summary>
    Header,

    /// <summary>The <c>?tenant=</c> query string.</summary>
    Query,

    /// <summary>The leftmost subdomain label.</summary>
    Subdomain,

    /// <summary>The continuity cookie.</summary>
    Cookie
}

/// <summary>
/// A candidate tenant slug together with the surface it was read from. <see cref="Value"/> is a
/// hint only - <c>TenantResolutionMiddleware</c> still has to look it up and authorize membership
/// before it means anything.
/// </summary>
public readonly record struct TenantHint(string? Value, TenantHintSource Source);

/// <summary>
/// A pluggable strategy that extracts a *candidate* tenant slug from a request.
/// Resolution order is the DI registration order; the first non-empty hint wins.
/// </summary>
public interface ITenantResolver
{
    TenantHint Resolve(HttpContext context);
}
