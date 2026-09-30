using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Acentra.Infrastructure.TenantData;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddTenantData(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // Validated (and therefore fails fast) at composition time rather than on the first query.
        services.AddSingleton(TenantConnectionStrings.FromConfiguration(configuration));

        services.AddScoped<ITenantDbContextFactory, TenantDbContextFactory>();
        services.AddScoped<ITenantProvisioner, TenantProvisioner>();

        // Development-only: brings each registered tenant's database up to date at startup.
        services.AddHostedService<TenantDatabaseInitializer>();

        // AppDbContext is deliberately NOT registered. It is scoped to a tenant, and the tenant is
        // mutable for the lifetime of a Blazor circuit, so the only safe way to obtain one is
        // ITenantDbContextFactory.Create() per unit of work.
        return services;
    }
}
