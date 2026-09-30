using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace Acentra.IntegrationTests;

/// <summary>
/// Test-only connection configuration.
///
/// <para>
/// The application's tenant connection template produces one Npgsql pool <em>per database name</em>,
/// and Npgsql's default <c>Maximum Pool Size</c> is 100. The suite provisions a GUID-suffixed
/// throwaway database per tenant, so a single parallel run can create far more pools than Postgres
/// (<c>max_connections = 100</c>) can serve — the observed symptom is
/// <c>FATAL: sorry, too many clients already</c>, followed by 100-second request timeouts.
/// </para>
///
/// <para>
/// This helper appends a small <c>Maximum Pool Size</c> to the test-side connection strings so the
/// whole suite's connection count stays bounded no matter how many throwaway databases it creates.
/// It is applied only by the tests: production and development keep the template exactly as it is
/// in <c>appsettings*.json</c>. Non-test hosts that need it call <see cref="ApplyPoolCap"/> from
/// their <c>ConfigureWebHost</c>.
/// </para>
/// </summary>
internal static class TestDatabase
{
    /// <summary>
    /// Cap for per-tenant pools. Small on purpose: the suite opens a pool per throwaway database,
    /// and a handful of connections per tenant is ample for tests that run one operation at a time.
    /// </summary>
    public const int TenantPoolSize = 5;

    /// <summary>
    /// Cap for the control-plane pool, which every test host in the process shares. Higher than the
    /// tenant cap because the hourly/parallel control-plane traffic is concentrated in this one pool.
    /// </summary>
    public const int ControlPlanePoolSize = 25;

    private const string TenantTemplateKey = "ConnectionStrings:TenantTemplate";
    private const string ControlPlaneKey = "ConnectionStrings:ControlPlane";

    /// <summary>
    /// Loads the same configuration a test host would see (dev then base JSON, then environment
    /// variables) and overlays the capped connection strings last, so they win.
    /// </summary>
    public static IConfiguration Load()
    {
        var builder = new ConfigurationBuilder()
            .AddJsonFile(DevSettingsPath(), optional: false)
            .AddJsonFile(BaseSettingsPath(), optional: false)
            .AddEnvironmentVariables();

        builder.AddInMemoryCollection(Capped(builder.Build()));

        return builder.Build();
    }

    /// <summary>
    /// Overlays the capped connection strings onto a test host's configuration. Uses
    /// <see cref="IWebHostBuilder.UseSetting"/> for the same reason the existing forwarded-headers
    /// test does: a host setting is visible to <c>builder.Configuration</c> when <c>Program.cs</c>
    /// reads it, unlike a late <c>ConfigureAppConfiguration</c> callback.
    /// </summary>
    public static void ApplyPoolCap(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var baseConfiguration = new ConfigurationBuilder()
            .AddJsonFile(DevSettingsPath(), optional: false)
            .AddJsonFile(BaseSettingsPath(), optional: false)
            .AddEnvironmentVariables()
            .Build();

        foreach (var (key, value) in Capped(baseConfiguration))
        {
            builder.UseSetting(key, value);
        }
    }

    private static IEnumerable<KeyValuePair<string, string?>> Capped(IConfiguration configuration) =>
    [
        new(
            TenantTemplateKey,
            CapPool(configuration.GetConnectionString("TenantTemplate"), TenantPoolSize)),
        new(
            ControlPlaneKey,
            CapPool(configuration.GetConnectionString("ControlPlane"), ControlPlanePoolSize))
    ];

    private static string? CapPool(string? connectionString, int maxPoolSize) =>
        string.IsNullOrWhiteSpace(connectionString)
            ? connectionString
            : $"{connectionString};Maximum Pool Size={maxPoolSize}";

    private static string DevSettingsPath() =>
        Path.Combine(RepositoryRoot(), "src", "Acentra.Web", "appsettings.Development.json");

    private static string BaseSettingsPath() =>
        Path.Combine(RepositoryRoot(), "src", "Acentra.Web", "appsettings.json");

    /// <summary>Repository root, located by the solution marker so path-relative I/O is stable.</summary>
    private static string RepositoryRoot()
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
}
