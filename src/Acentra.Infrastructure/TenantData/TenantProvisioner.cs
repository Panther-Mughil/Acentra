using Acentra.Domain.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Acentra.Infrastructure.TenantData;

/// <summary>
/// Creates the database that backs a tenant and brings it up to the current tenant schema.
/// Idempotent in both halves: an existing database is reused, and <c>Migrate</c> is a no-op when
/// there is nothing pending — so it is safe to call on every start and to call concurrently.
/// </summary>
public sealed class TenantProvisioner(
    TenantConnectionStrings connections,
    ITenantDbContextFactory factory,
    ILogger<TenantProvisioner> logger) : ITenantProvisioner
{
    /// <summary>PostgreSQL SQLSTATE <c>duplicate_database</c>.</summary>
    private const string DuplicateDatabaseSqlState = "42P04";

    private readonly TenantConnectionStrings _connections = connections;
    private readonly ITenantDbContextFactory _factory = factory;
    private readonly ILogger<TenantProvisioner> _logger = logger;

    public async Task ProvisionAsync(TenantDescriptor tenant, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tenant);

        // The control plane is the source of truth for the database name. An empty one is refused
        // rather than guessed at: guessing could route a tenant at another tenant's database.
        if (string.IsNullOrWhiteSpace(tenant.DatabaseName))
        {
            throw new InvalidOperationException(
                $"Tenant '{tenant.Slug}' has no DatabaseName. The control plane's Tenant.DatabaseName " +
                "is the source of truth for a tenant's database and must be populated before provisioning.");
        }

        var databaseName = TenantDatabaseName.Normalize(tenant.DatabaseName);

        await EnsureDatabaseAsync(databaseName, ct);

        // Migrations bypass query filters by design and only touch this tenant's own database.
        await using var db = _factory.CreateForTenant(tenant);
        await db.Database.MigrateAsync(ct);

        _logger.LogInformation(
            "Tenant '{Slug}' database '{Database}' is present and migrated.",
            tenant.Slug,
            databaseName);
    }

    private async Task EnsureDatabaseAsync(string databaseName, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(_connections.ForMaintenance());
        await connection.OpenAsync(ct);

        if (await ExistsAsync(connection, databaseName, ct))
        {
            _logger.LogDebug("Tenant database '{Database}' already exists.", databaseName);
            return;
        }

        try
        {
            // The single raw SQL statement in the application path. CREATE DATABASE cannot be
            // parameterised and cannot run inside a transaction, so the name is not escaped but
            // *constrained*: TenantDatabaseName.Normalize guarantees ^[a-z_][a-z0-9_]{0,62}$ before
            // this point, which makes the quoted identifier safe. It deliberately connects to the
            // maintenance database, never to a tenant's database.
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", connection);
            await create.ExecuteNonQueryAsync(ct);

            _logger.LogInformation("Created tenant database '{Database}'.", databaseName);
        }
        catch (PostgresException ex) when (ex.SqlState == DuplicateDatabaseSqlState)
        {
            // A concurrent provisioner won the race. The database exists, which is the postcondition.
            _logger.LogDebug("Tenant database '{Database}' was created concurrently.", databaseName);
        }
    }

    private static async Task<bool> ExistsAsync(
        NpgsqlConnection connection,
        string databaseName,
        CancellationToken ct)
    {
        await using var exists = new NpgsqlCommand(
            "SELECT 1 FROM pg_database WHERE datname = $1",
            connection);

        exists.Parameters.AddWithValue(databaseName);

        return await exists.ExecuteScalarAsync(ct) is not null;
    }
}
