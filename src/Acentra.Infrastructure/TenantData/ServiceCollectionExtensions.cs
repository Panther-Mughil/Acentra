using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Acentra.Infrastructure.TenantData;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddTenantData(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddScoped<ITenantDbContextFactory, TenantDbContextFactory>();
        return services;
    }
}
