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
    /// is echoed in this cookie to let the circuit re-resolve - never as authority.
    /// </summary>
    public const string CookieName = "acentra_tenant";

    public static readonly TimeSpan CookieLifetime = TimeSpan.FromHours(8);

    /// <summary>
    /// Key under which <see cref="TenantResolutionMiddleware"/> stashes the resolved
    /// descriptor in <c>HttpContext.Items</c> for the circuit handler to pick up.
    /// </summary>
    public const string HttpContextItemKey = "acentra.resolved-tenant";

    /// <summary>
    /// The one and only body returned for every "you cannot use this tenant" outcome - unknown
    /// tenant, suspended tenant, non-member, unparseable slug and unusable identity. It is a
    /// constant that never contains the supplied slug, so the response is byte-for-byte
    /// indistinguishable between causes and cannot be used to enumerate tenants. The precise
    /// cause is logged server-side at Warning.
    /// </summary>
    public const string AccessDeniedMessage = "Access to the requested tenant was denied.";

    /// <summary>Content type of the uniform denial body.</summary>
    public const string AccessDeniedContentType = "text/plain; charset=utf-8";

    /// <summary>
    /// Configuration key for the circuit re-authorization interval (REQ-009). Bound at
    /// <c>Tenant:RevalidationInterval</c>; a <see cref="System.TimeSpan"/> string such as
    /// <c>00:05:00</c>.
    /// </summary>
    public const string RevalidationIntervalKey = "Tenant:RevalidationInterval";

    /// <summary>
    /// Default bound on how long a circuit's tenant authorization may go unchecked. A Blazor
    /// circuit makes no per-interaction HTTP request, so this interval is the *only* thing that
    /// re-checks membership and tenant status - it is a staleness bound, not instant revocation.
    /// </summary>
    public static readonly TimeSpan DefaultRevalidationInterval = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Positive floor the configured interval is clamped to. A zero or negative value must never
    /// silently disable the guarantee, so it is raised to this instead.
    /// </summary>
    public static readonly TimeSpan MinimumRevalidationInterval = TimeSpan.FromSeconds(30);
}
