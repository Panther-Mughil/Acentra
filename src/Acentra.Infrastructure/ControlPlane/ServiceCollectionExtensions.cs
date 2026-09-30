using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Acentra.Infrastructure.ControlPlane;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddControlPlane(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("ControlPlane") ?? string.Empty;
        services.AddDbContext<ControlPlaneDbContext>(options => options.UseNpgsql(connectionString));
        services.AddScoped<ITenantRegistry, TenantRegistry>();
        return services;
    }
}
