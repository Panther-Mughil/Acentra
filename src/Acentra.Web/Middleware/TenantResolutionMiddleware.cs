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
/// Flow (fixed order):
/// <list type="number">
///   <item>read the hint: <c>X-Tenant</c> header, else <c>?tenant=</c>, else subdomain, else the continuity cookie;</item>
///   <item>look the normalised slug up through <see cref="ITenantRegistry"/> — unknown → 400;</item>
///   <item>authorize: the authenticated principal must have a membership → otherwise 403;</item>
///   <item>publish the tenant on the scoped <see cref="ITenantContext"/> (and <c>HttpContext.Items</c>);</item>
///   <item>the circuit handler seeds per-circuit <see cref="TenantState"/> from those Items.</item>
/// </list>
///
/// Fail-closed rules: an unresolved tenant never means "all tenants"; there is no default
/// tenant; the client-supplied slug is only ever a hint — membership is authority. Requests
/// to tenant-agnostic paths still resolve a hint when one is present but never fail.
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

        var hint = resolvers
            .Select(resolver => resolver.Resolve(context))
            .FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate));

        var slug = TenantSlug.TryNormalize(hint);

        if (slug is null)
        {
            state.Clear();

            if (requiresTenant)
            {
                await FailAsync(context, StatusCodes.Status400BadRequest, "No resolvable tenant hint was supplied.");
                return;
            }

            await next(context);
            return;
        }

        var tenant = await registry.FindBySlugAsync(slug, context.RequestAborted);

        if (tenant is null)
        {
            state.Clear();
            ClearCookie(context);

            if (requiresTenant)
            {
                await FailAsync(context, StatusCodes.Status400BadRequest, $"Unknown tenant '{slug}'.");
                return;
            }

            await next(context);
            return;
        }

        if (context.User.Identity?.IsAuthenticated != true)
        {
            state.Clear();
            ClearCookie(context);

            if (requiresTenant)
            {
                await ChallengeAsync(context);
                return;
            }

            await next(context);
            return;
        }

        var userId = GetUserId(context.User);

        if (userId is null)
        {
            state.Clear();
            ClearCookie(context);

            if (requiresTenant)
            {
                await FailAsync(context, StatusCodes.Status403Forbidden, "The authenticated principal has no usable identity.");
                return;
            }

            await next(context);
            return;
        }

        var memberships = await registry.FindForUserAsync(userId.Value, context.RequestAborted);

        if (!memberships.Any(m => m.Id == tenant.Id))
        {
            state.Clear();
            ClearCookie(context);

            // Membership is authority: a valid hint for a tenant the user does not belong to
            // is a forbidden request even on a tenant-agnostic path.
            await FailAsync(context, StatusCodes.Status403Forbidden, $"Not a member of tenant '{slug}'.");
            return;
        }

        state.Set(tenant.Id, tenant.Slug);
        context.Items[TenantResolutionConstants.HttpContextItemKey] = tenant;

        PublishCookie(context, tenant.Slug);

        logger.LogDebug("Resolved tenant '{Slug}' ({TenantId}) for {Method} {Path}.",
            tenant.Slug, tenant.Id, context.Request.Method, context.Request.Path);

        await next(context);
    }

    private static Guid? GetUserId(System.Security.Claims.ClaimsPrincipal user)
    {
        var value = user.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

        return Guid.TryParse(value, out var id) ? id : null;
    }

    private async Task FailAsync(HttpContext context, int statusCode, string reason)
    {
        if (context.Response.HasStarted)
        {
            return;
        }

        context.Response.StatusCode = statusCode;
        await context.Response.WriteAsync(reason);
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
