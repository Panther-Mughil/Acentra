using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Acentra.Infrastructure.ControlPlane;

public static class ServiceCollectionExtensions
{
    public const string MigrationsAssembly = "Acentra.Infrastructure";

    public static IServiceCollection AddControlPlane(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("ControlPlane");

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "Connection string 'ControlPlane' is not configured. The control-plane database " +
                "holds Identity users, tenants and memberships and is required.");
        }

        services.AddDbContext<ControlPlaneDbContext>(options =>
            options.UseNpgsql(
                connectionString,
                npgsql => npgsql.MigrationsAssembly(MigrationsAssembly)));

        services.AddScoped<ITenantRegistry, TenantRegistry>();

        // Identity *core* lives here (provider-agnostic). The authentication schemes and
        // cookies live in the Web seam — this project has no ASP.NET Core shared framework.
        services.AddIdentityCore<AppUser>(options =>
            {
                options.User.RequireUniqueEmail = true;
                options.Password.RequiredLength = 8;
                options.Password.RequireNonAlphanumeric = true;
                options.SignIn.RequireConfirmedAccount = false;
            })
            .AddRoles<IdentityRole<Guid>>()
            .AddEntityFrameworkStores<ControlPlaneDbContext>();

        return services;
    }
}
