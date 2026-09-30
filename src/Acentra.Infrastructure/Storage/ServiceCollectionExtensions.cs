using Acentra.Domain.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Acentra.Infrastructure.Storage;

public static class ServiceCollectionExtensions
{
    private const string SectionName = "Storage";

    /// <summary>
    /// Binds <see cref="StorageOptions"/> from the <c>Storage</c> configuration section and registers
    /// the selected provider as <see cref="IFileStorage"/>. Options are immutable after binding and
    /// both implementations are stateless (the S3 client is thread-safe), so singletons are correct.
    /// </summary>
    public static IServiceCollection AddFileStorage(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = new StorageOptions();
        configuration.GetSection(SectionName).Bind(options);

        if (string.IsNullOrWhiteSpace(options.Provider))
        {
            options.Provider = "S3";
        }

        services.AddSingleton(options);
        services.AddSingleton<IStorageOptions>(options);

        if (string.Equals(options.Provider, "Local", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<IFileStorage, LocalFileStorage>();
        }
        else if (string.Equals(options.Provider, "S3", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<IFileStorage, S3FileStorage>();
        }
        else
        {
            throw new InvalidOperationException(
                $"Unknown storage provider '{options.Provider}'. Expected 'S3' or 'Local' (Storage:Provider).");
        }

        return services;
    }
}
