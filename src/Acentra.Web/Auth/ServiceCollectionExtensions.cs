using Acentra.Infrastructure.ControlPlane;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Acentra.Web.Auth;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Web-side half of the control plane. Registers everything that needs the ASP.NET Core
    /// shared framework: the Identity authentication schemes/cookies, the tenant hint
    /// strategies, the HTTP-request -> circuit bridge, and the dev startup service.
    ///
    /// <see cref="Acentra.Infrastructure.ControlPlane.ServiceCollectionExtensions.AddControlPlane"/>
    /// cannot host these — <c>Acentra.Infrastructure</c> is a plain class library with no
    /// ASP.NET Core shared framework (verified).
    /// </summary>
    public static IServiceCollection AddTenantResolution(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();

        // Identity authentication schemes + cookies.
        services.AddAuthentication(IdentityConstants.ApplicationScheme)
            .AddIdentityCookies();

        // SignInManager is an ASP.NET Core type, so it is added here rather than in AddControlPlane.
        services.AddIdentityCore<AppUser>().AddSignInManager();

        // Hint strategies. Registration order is precedence:
        // X-Tenant header > ?tenant= query > subdomain > continuity cookie.
        services.AddSingleton<ITenantResolver, HeaderTenantResolver>();
        services.AddSingleton<ITenantResolver, QueryTenantResolver>();
        services.AddSingleton<ITenantResolver, SubdomainTenantResolver>();
        services.AddSingleton<ITenantResolver, CookieTenantResolver>();

        // One handler per circuit; holds this circuit's TenantState.
        services.AddScoped<CircuitHandler, TenantCircuitHandler>();

        // Applies control-plane migrations and the dev seed (Development only).
        services.AddHostedService<ControlPlaneInitializer>();

        return services;
    }
}
