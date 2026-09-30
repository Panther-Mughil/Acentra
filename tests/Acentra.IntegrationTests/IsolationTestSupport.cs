using Acentra.Domain.Abstractions;
using Acentra.Infrastructure.TenantData;
using Acentra.Web.Auth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Acentra.IntegrationTests;

/// <summary>
/// Shared plumbing for the REQ-006 isolation matrix.
///
/// It deliberately goes through the committed <see cref="TenantConnectionStrings"/> /
/// <see cref="TenantDbContextFactory"/> / <see cref="TenantProvisioner"/> rather than
/// re-implementing routing, so the matrix exercises the same code the application does. Every
/// tenant database it creates is GUID-suffixed and dropped in teardown, so the developer's real
/// <c>acentra_acme</c> / <c>acentra_globex</c> databases are never touched and runs never collide.
/// </summary>
internal static class IsolationTestSupport
{
    public static IConfiguration Configuration { get; } = LoadConfiguration();

    public static TenantConnectionStrings Connections { get; } =
        TenantConnectionStrings.FromConfiguration(Configuration);

    /// <summary>Repository root, located by the solution marker so path-relative I/O is stable.</summary>
    public static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Acentra.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the repository root (Acentra.slnx).");
    }

    /// <summary>An <see cref="AppDbContext"/> for an explicitly described tenant, via the real factory.</summary>
    public static AppDbContext CreateFor(TenantDescriptor tenant) =>
        new TenantDbContextFactory(new TenantState(), Connections).CreateForTenant(tenant);

    /// <summary>
    /// Builds a context against one database while carrying another tenant. This is the only way to
    /// express "the connection and the tenant disagree" — the situation the global query filter
    /// exists to contain — and it uses no raw SQL and no production bypass.
    /// </summary>
    public static AppDbContext CreateForTenantAgainstDatabase(TenantDescriptor tenant, string databaseName)
    {
        var scope = new ResolvedTenantContext(tenant.Id, tenant.Slug);

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(Connections.ForDatabase(databaseName))
            .AddInterceptors(new TenantStampInterceptor(scope))
            .Options;

        return new AppDbContext(options, scope);
    }

    /// <summary>
    /// Drops a tenant database if it exists, terminating any lingering connections. Safe to call for
    /// a database that was never created.
    /// </summary>
    public static async Task DropDatabaseAsync(string databaseName)
    {
        if (string.IsNullOrWhiteSpace(databaseName))
        {
            return;
        }

        // Normalize guarantees ^[a-z_][a-z0-9_]*$, so quoting the identifier is safe.
        var normalized = TenantDatabaseName.Normalize(databaseName);

        await using var connection = new NpgsqlConnection(Connections.ForMaintenance());
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(
            $"DROP DATABASE IF EXISTS \"{normalized}\" WITH (FORCE)", connection);

        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Verifies Postgres is reachable, failing loudly with the fix rather than letting the isolation
    /// suite look "green because nothing ran" (plan §8).
    /// </summary>
    public static async Task AssertPostgresReachableAsync()
    {
        try
        {
            await using var connection = new NpgsqlConnection(Connections.ForMaintenance());
            await connection.OpenAsync();
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                "PostgreSQL is not reachable, so the tenant-isolation matrix cannot run. Start it with " +
                "`podman-compose up -d` (container 'acentra-postgres') and re-run. " +
                $"Underlying error: {exception.GetType().Name}: {exception.Message}",
                exception);
        }
    }

    private static IConfiguration LoadConfiguration() =>
        new ConfigurationBuilder()
            .AddJsonFile(
                Path.Combine(RepositoryRoot(), "src", "Acentra.Web", "appsettings.Development.json"),
                optional: false)
            .AddJsonFile(
                Path.Combine(RepositoryRoot(), "src", "Acentra.Web", "appsettings.json"),
                optional: false)
            .AddEnvironmentVariables()
            .Build();
}

/// <summary>
/// A throwaway tenant whose database is created on demand and dropped in teardown. The database
/// name is GUID-suffixed, so parallel runs and repeated runs never collide and a leftover from a
/// crashed run is never reused.
/// </summary>
public sealed class ThrowawayTenant
{
    private readonly string _databaseName;

    private ThrowawayTenant(TenantDescriptor descriptor, string databaseName)
    {
        Descriptor = descriptor;
        _databaseName = databaseName;
    }

    public TenantDescriptor Descriptor { get; }

    public static ThrowawayTenant Create(string label)
    {
        var labelSafe = label.ToLowerInvariant().Replace('-', '_');
        var suffix = Guid.NewGuid().ToString("N")[..12];

        var slug = $"req006-{label.ToLowerInvariant().Replace('_', '-')}-{suffix}";
        if (slug.Length > 40)
        {
            throw new ArgumentException($"Slug '{slug}' exceeds the tenant slug limit.", nameof(label));
        }

        var databaseName = TenantDatabaseName.Normalize($"acentra_req006_{labelSafe}_{suffix}");

        return new ThrowawayTenant(
            new TenantDescriptor(Guid.NewGuid(), slug, $"REQ-006 {label}", databaseName),
            databaseName);
    }

    /// <summary>Creates the database and migrates it to the current tenant schema.</summary>
    public async Task ProvisionAsync()
    {
        await IsolationTestSupport.AssertPostgresReachableAsync();

        var provisioner = new TenantProvisioner(
            IsolationTestSupport.Connections,
            new TenantDbContextFactory(new TenantState(), IsolationTestSupport.Connections),
            NullLogger<TenantProvisioner>.Instance);

        await provisioner.ProvisionAsync(Descriptor, CancellationToken.None);
    }

    /// <summary>Drops the database if it exists, terminating any lingering connections.</summary>
    public Task DropAsync() => IsolationTestSupport.DropDatabaseAsync(_databaseName);
}

/// <summary>
/// A minimal tenant-agnostic host environment for the direct <c>TenantResolutionMiddleware</c>
/// probes: nothing under the web root exists, so every probe path is treated as tenant-scoped (the
/// fail-closed default) exactly as it would be in production.
/// </summary>
internal sealed class ProbeWebHostEnvironment : IWebHostEnvironment
{
    public string ApplicationName { get; set; } = "Acentra.Web";

    public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();

    public string WebRootPath { get; set; } = string.Empty;

    public string EnvironmentName { get; set; } = "Development";

    public string ContentRootPath { get; set; } = string.Empty;

    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
