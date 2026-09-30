using Microsoft.AspNetCore.Http;

namespace Acentra.Web.Auth;

/// <summary>
/// A pluggable strategy that extracts a *candidate* tenant slug from a request.
/// The returned value is a hint only — <c>TenantResolutionMiddleware</c> still has to
/// look it up and authorize membership before it means anything.
/// Resolution order is the DI registration order; the first non-null hint wins.
/// </summary>
public interface ITenantResolver
{
    string? Resolve(HttpContext context);
}
