using Acentra.Domain.Abstractions;
using Acentra.Infrastructure.ControlPlane;
using Acentra.Web.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Acentra.Web.Middleware;

/// <summary>
/// Turns a request hint into a resolved, <em>authorized</em> tenant — or fails closed.
///
/// Flow (fixed order, authentication <em>first</em>):
/// <list type="number">
///   <item>read the winning hint and its <see cref="TenantHintSource"/>: <c>X-Tenant</c> header,
///         else <c>?tenant=</c>, else subdomain, else the continuity cookie;</item>
///   <item>if the request is not authenticated, challenge it (401 for <c>/api/*</c>, otherwise the
///         Identity redirect) or pass through on a tenant-agnostic path — <strong>no tenant lookup
///         runs at all</strong>, so nothing about tenant existence can be probed;</item>
///   <item>authenticated with no hint → 400 on a tenant-scoped path, pass through otherwise;</item>
///   <item>authenticated with a hint → look the tenant up <em>and</em> the caller's memberships (both
///         unconditionally, so timing cannot discriminate between the causes) → publish the tenant on
///         the scoped <see cref="ITenantContext"/> (and <c>HttpContext.Items</c>) or deny;</item>
///   <item>the circuit handler seeds per-circuit <see cref="TenantState"/> from those Items.</item>
/// </list>
///
/// Anti-enumeration: every denial cause produces the same, constant, slug-free
/// <see cref="TenantResolutionConstants.AccessDeniedMessage"/> body. Fail-closed rules: an
/// unresolved tenant never means "all tenants"; there is no default tenant; the client-supplied
/// slug is only ever a hint — membership is authority. Tenant-agnostic paths never fail because of
/// a hint, and never clear the continuity cookie because of one.
/// </summary>
public sealed class TenantResolutionMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(
        HttpContext context,
        IEnumerable<ITenantResolver> resolvers,
        ITenantRegistry registry,
        TenantState state,
        IWebHostEnvironment environment,
        ILogger<TenantResolutionMiddleware> logger)
    {
        var requiresTenant = !TenantAgnosticPaths.IsTenantAgnostic(context.Request.Path, environment);

        var hint = ResolveHint(resolvers, context);

        // 1. Authenticate first. An unauthenticated caller can never probe a tenant: no lookup
        //    runs, no state is written, so a valid slug and an unknown slug are indistinguishable.
        if (context.User.Identity?.IsAuthenticated != true)
        {
            state.Clear();

            if (requiresTenant)
            {
                await ChallengeAsync(context);
                return;
            }

            await next(context);
            return;
        }

        // 2. Authenticated, no hint. This is the ordinary "pick a tenant" case on a tenant-scoped
        //    path; on a tenant-agnostic path (/, /Account/Login, …) it is entirely normal.
        if (string.IsNullOrWhiteSpace(hint.Value))
        {
            state.Clear();

            if (requiresTenant)
            {
                await FailNoHintAsync(context);
                return;
            }

            await next(context);
            return;
        }

        // 3. Authenticated with a hint. Always run BOTH lookups before deciding so response timing
        //    does not discriminate "unknown tenant" from "not a member".
        var slug = TenantSlug.TryNormalize(hint.Value);
        var tenant = await registry.FindBySlugAsync(slug ?? hint.Value, context.RequestAborted);
        var userId = GetUserId(context.User);
        var memberships = await registry.FindForUserAsync(userId ?? Guid.Empty, context.RequestAborted);

        if (slug is null)
        {
            logger.LogWarning(
                "Tenant hint from {Source} is not a valid slug; denying.", hint.Source);
            await DenyAsync(context, requiresTenant, hint, state, next);
            return;
        }

        if (tenant is null)
        {
            logger.LogWarning(
                "Tenant '{Slug}' is unknown or not active; denying.", slug);
            await DenyAsync(context, requiresTenant, hint, state, next);
            return;
        }

        if (userId is null)
        {
            logger.LogWarning(
                "Authenticated principal has no usable NameIdentifier; denying tenant '{Slug}'.", slug);
            await DenyAsync(context, requiresTenant, hint, state, next);
            return;
        }

        if (!memberships.Any(m => m.Id == tenant.Id))
        {
            logger.LogWarning(
                "User {UserId} is not a member of tenant '{Slug}'; denying.", userId.Value, slug);
            await DenyAsync(context, requiresTenant, hint, state, next);
            return;
        }

        state.Set(tenant.Id, tenant.Slug);
        context.Items[TenantResolutionConstants.HttpContextItemKey] = tenant;

        PublishCookie(context, tenant.Slug);

        logger.LogDebug("Resolved tenant '{Slug}' ({TenantId}) for {Method} {Path}.",
            tenant.Slug, tenant.Id, context.Request.Method, context.Request.Path);

        await next(context);
    }

    /// <summary>
    /// The first non-empty hint wins; DI registration order is precedence
    /// (header &gt; query &gt; subdomain &gt; cookie).
    /// </summary>
    private static TenantHint ResolveHint(IEnumerable<ITenantResolver> resolvers, HttpContext context)
    {
        foreach (var resolver in resolvers)
        {
            var candidate = resolver.Resolve(context);

            if (!string.IsNullOrWhiteSpace(candidate.Value))
            {
                return candidate;
            }
        }

        return new TenantHint(null, TenantHintSource.None);
    }

    /// <summary>
    /// One denial path for every cause. On a tenant-scoped path it returns the uniform, slug-free
    /// 403; on a tenant-agnostic path it simply ignores the hint and continues. Because the winning
    /// source is known, the continuity cookie is cleared only for a cookie-sourced failure — a
    /// header/query/subdomain failure can never wipe a valid selection.
    /// </summary>
    private static async Task DenyAsync(
        HttpContext context,
        bool requiresTenant,
        TenantHint hint,
        TenantState state,
        RequestDelegate next)
    {
        state.Clear();

        if (!requiresTenant)
        {
            // A bad or foreign hint on a tenant-agnostic path is ignored: no 403 and no cookie
            // clearing, which closes the "crafted ?tenant= wipes the cookie" vector.
            await next(context);
            return;
        }

        if (hint.Source == TenantHintSource.Cookie)
        {
            ClearCookie(context);
        }

        await FailUniformAsync(context);
    }

    private static Guid? GetUserId(System.Security.Claims.ClaimsPrincipal user)
    {
        var value = user.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

        return Guid.TryParse(value, out var id) ? id : null;
    }

    /// <summary>
    /// The uniform denial. Status, body and content type are constant and cause-independent, and
    /// the body never contains the supplied slug — so the response is byte-identical for an unknown
    /// tenant, a suspended tenant, a non-member or an unusable identity.
    /// </summary>
    private static async Task FailUniformAsync(HttpContext context)
    {
        if (context.Response.HasStarted)
        {
            return;
        }

        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.ContentType = TenantResolutionConstants.AccessDeniedContentType;
        await context.Response.WriteAsync(
            TenantResolutionConstants.AccessDeniedMessage, context.RequestAborted);
    }

    private static async Task FailNoHintAsync(HttpContext context)
    {
        if (context.Response.HasStarted)
        {
            return;
        }

        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        context.Response.ContentType = TenantResolutionConstants.AccessDeniedContentType;
        await context.Response.WriteAsync(
            "No resolvable tenant hint was supplied.", context.RequestAborted);
    }

    private static async Task ChallengeAsync(HttpContext context)
    {
        // API clients get an unambiguous status code; browsers get the Identity cookie
        // handler's redirect to the (tenant-agnostic) login page.
        if (context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
        {
            if (!context.Response.HasStarted)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            }

            return;
        }

        await context.ChallengeAsync();
    }

    private static void PublishCookie(HttpContext context, string slug)
    {
        // Only on ordinary GETs. Never on the SignalR handshake: the cookie exists to carry the
        // selection *into* that handshake, and setting cookies on a WebSocket negotiate is noise.
        if (!HttpMethods.IsGet(context.Request.Method))
        {
            return;
        }

        if (context.Request.Path.StartsWithSegments("/_blazor", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        context.Response.Cookies.Append(
            TenantResolutionConstants.CookieName,
            slug,
            new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Lax,
                Secure = context.Request.IsHttps,
                Path = "/",
                MaxAge = TenantResolutionConstants.CookieLifetime
            });
    }

    private static void ClearCookie(HttpContext context)
    {
        if (context.Response.HasStarted)
        {
            return;
        }

        context.Response.Cookies.Delete(
            TenantResolutionConstants.CookieName,
            new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Lax,
                Secure = context.Request.IsHttps,
                Path = "/"
            });
    }
}
