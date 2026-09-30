using Microsoft.Extensions.DependencyInjection;

namespace Acentra.Infrastructure.Inventory;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddInventory(this IServiceCollection services)
    {
        services.AddScoped<IInventoryService, InventoryService>();
        return services;
    }
}
