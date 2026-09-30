using Acentra.Domain.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Acentra.Infrastructure.Storage;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddFileStorage(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddSingleton<StorageOptions>();
        services.AddSingleton<IStorageOptions>(sp => sp.GetRequiredService<StorageOptions>());
        services.AddScoped<IFileStorage, S3FileStorage>();
        return services;
    }
}
