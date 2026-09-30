namespace Acentra.Web.Auth;

/// <summary>
/// Shared names for the tenant hint surface and the request/circuit bridge.
/// </summary>
public static class TenantResolutionConstants
{
    /// <summary>Default hint: header carrying the tenant slug.</summary>
    public const string HeaderName = "X-Tenant";

    /// <summary>Secondary, JS-free hint (useful for a persisted switch and for HTTP tests).</summary>
    public const string QueryKey = "tenant";

    /// <summary>
    /// Continuity hint. The page GET and the SignalR <c>/_blazor</c> handshake are separate
    /// HttpContexts and a WebSocket handshake carries no custom header, so the resolved slug
    /// is echoed in this cookie to let the circuit re-resolve — never as authority.
    /// </summary>
    public const string CookieName = "acentra_tenant";

    public static readonly TimeSpan CookieLifetime = TimeSpan.FromHours(8);

    /// <summary>
    /// Key under which <see cref="TenantResolutionMiddleware"/> stashes the resolved
    /// descriptor in <c>HttpContext.Items</c> for the circuit handler to pick up.
    /// </summary>
    public const string HttpContextItemKey = "acentra.resolved-tenant";
}
