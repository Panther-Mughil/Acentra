using Acentra.Domain.Abstractions;
using Acentra.Infrastructure.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Acentra.UnitTests.Storage;

/// <summary>
/// REQ-004 §4: <c>AddFileStorage</c> binds the <c>Storage</c> section and selects the provider from
/// configuration alone — no compile-time switch, and an unknown provider fails loudly rather than
/// silently falling back.
/// </summary>
public sealed class ServiceCollectionExtensionsTests
{
    [Fact]
    public void AddFileStorage_BindsTheStorageSection()
    {
        using var provider = Build(new Dictionary<string, string?>
        {
            ["Storage:Provider"] = "Local",
            ["Storage:LocalRoot"] = "/tmp/acentra-bound-root",
            ["Storage:Endpoint"] = "http://minio.internal:9000",
            ["Storage:Bucket"] = "bound-bucket",
            ["Storage:AccessKey"] = "bound-access",
            ["Storage:SecretKey"] = "bound-secret",
            ["Storage:UseSsl"] = "true",
            ["Storage:Region"] = "eu-west-1",
        });

        var options = provider.GetRequiredService<IStorageOptions>();

        Assert.Equal("Local", options.Provider);
        Assert.Equal("/tmp/acentra-bound-root", options.LocalRoot);
        Assert.Equal("http://minio.internal:9000", options.Endpoint);
        Assert.Equal("bound-bucket", options.Bucket);
        Assert.Equal("bound-access", options.AccessKey);
        Assert.Equal("bound-secret", options.SecretKey);
        Assert.True(options.UseSsl);
        Assert.Equal("eu-west-1", options.Region);
    }

    [Fact]
    public void AddFileStorage_SelectsTheLocalProviderFromConfig()
    {
        using var provider = Build(new Dictionary<string, string?>
        {
            ["Storage:Provider"] = "local",
            ["Storage:LocalRoot"] = Path.Combine(Path.GetTempPath(), "acentra-provider-selection"),
        });

        Assert.IsType<LocalFileStorage>(provider.GetRequiredService<IFileStorage>());
    }

    [Fact]
    public void AddFileStorage_SelectsTheS3ProviderFromConfig()
    {
        using var provider = Build(new Dictionary<string, string?>
        {
            ["Storage:Provider"] = "S3",
        });

        Assert.IsType<S3FileStorage>(provider.GetRequiredService<IFileStorage>());
    }

    [Fact]
    public void AddFileStorage_DefaultsTheProviderToS3_WhenTheSectionIsAbsent()
    {
        using var provider = Build(new Dictionary<string, string?>());

        var options = provider.GetRequiredService<IStorageOptions>();
        Assert.Equal("S3", options.Provider);
        Assert.Equal("storage", options.LocalRoot);
        Assert.IsType<S3FileStorage>(provider.GetRequiredService<IFileStorage>());
    }

    [Fact]
    public void AddFileStorage_RejectsAnUnknownProvider()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Storage:Provider"] = "SeaweedFS" })
            .Build();

        var exception = Assert.Throws<InvalidOperationException>(
            () => services.AddFileStorage(configuration));

        Assert.Contains("SeaweedFS", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddFileStorage_RegistersFileStorageAsASingleton()
    {
        using var provider = Build(new Dictionary<string, string?> { ["Storage:Provider"] = "Local" });

        Assert.Same(provider.GetRequiredService<IFileStorage>(), provider.GetRequiredService<IFileStorage>());
    }

    private static ServiceProvider Build(IReadOnlyDictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();

        services.AddFileStorage(configuration);

        return services.BuildServiceProvider(validateScopes: true);
    }
}
