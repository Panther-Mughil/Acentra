using Acentra.Domain.Abstractions;
using Microsoft.Extensions.Configuration;

namespace Acentra.Infrastructure.TenantData;

/// <summary>
/// Turns the <c>ConnectionStrings:TenantTemplate</c> connection-string template — which contains the
/// <c>{0}</c> database-name placeholder — into a concrete connection string per tenant database.
/// Instances are immutable and safe to share; the *connections* they produce are not cached, because
/// the tenant (and therefore the target database) can change mid-circuit.
/// </summary>
public sealed class TenantConnectionStrings
{
    /// <summary>Configuration path of the template. Its only variable part is the database name.</summary>
    public const string ConfigurationKey = "ConnectionStrings:TenantTemplate";

    /// <summary>Placeholder replaced by the tenant database name. Exactly as it appears in config.</summary>
    public const string Placeholder = "{0}";

    /// <summary>
    /// Database used purely as a connection point for <c>CREATE DATABASE</c> (PostgreSQL requires an
    /// existing database to connect to; it cannot be created from a connection to itself).
    /// </summary>
    public const string MaintenanceDatabase = "postgres";

    private readonly string _template;

    public TenantConnectionStrings(string template)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            throw new ArgumentException(
                $"Connection string '{ConfigurationKey}' is not configured. Tenant data lives in one " +
                "database per tenant, so its template (with the " + Placeholder + " placeholder) is required.",
                nameof(template));
        }

        if (!template.Contains(Placeholder, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Connection string '{ConfigurationKey}' must contain the '{Placeholder}' database-name placeholder.",
                nameof(template));
        }

        _template = template;
    }

    /// <summary>Reads and validates the template from configuration (throws when it is missing).</summary>
    public static TenantConnectionStrings FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return new TenantConnectionStrings(configuration.GetConnectionString("TenantTemplate") ?? string.Empty);
    }

    /// <summary>Connection string for the tenant the ambient <see cref="ITenantContext"/> is bound to.</summary>
    public string For(ITenantContext tenant)
    {
        ArgumentNullException.ThrowIfNull(tenant);

        return ForDatabase(TenantDatabaseName.FromSlug(tenant.Slug));
    }

    /// <summary>Connection string for an explicitly described tenant (provisioning, startup, jobs).</summary>
    public string For(TenantDescriptor tenant)
    {
        ArgumentNullException.ThrowIfNull(tenant);

        return ForDatabase(TenantDatabaseName.Normalize(tenant.DatabaseName));
    }

    /// <summary>Connection string for an arbitrary (sanitised) database name.</summary>
    public string ForDatabase(string databaseName) =>
        _template.Replace(Placeholder, TenantDatabaseName.Normalize(databaseName), StringComparison.Ordinal);

    /// <summary>
    /// Connection used only to run <c>CREATE DATABASE</c>. It must never be used to read or write
    /// tenant data — that always goes through the per-tenant connection.
    /// </summary>
    public string ForMaintenance() => ForDatabase(MaintenanceDatabase);

    /// <summary>Redacted on purpose: a connection string carries credentials and must not be logged.</summary>
    public override string ToString() => $"{nameof(TenantConnectionStrings)} {{ template: <redacted> }}";
}
