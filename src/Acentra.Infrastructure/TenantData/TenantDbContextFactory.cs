using Acentra.Domain.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Acentra.Infrastructure.TenantData;

/// <summary>
/// Builds a short-lived <see cref="AppDbContext"/> bound to one tenant's database.
///
/// <para>
/// Options — and therefore the connection — are constructed <em>per call</em> and never cached:
/// <see cref="ITenantContext"/> is circuit-scoped and mutable, so the tenant can change mid-circuit
/// and a cached context (or a cached Npgsql data source keyed by tenant) would hand the next
/// interaction the previous tenant's database.
/// </para>
///
/// <para>
/// Reads and writes are both protected: the context carries the same tenant for its query filters
/// and for the <see cref="TenantStampInterceptor"/> attached to its options.
/// </para>
/// </summary>
public sealed class TenantDbContextFactory : ITenantDbContextFactory
{
    /// <summary>Migrations live in this assembly; the connection is dynamic, so it is set here.</summary>
    public const string MigrationsAssembly = "Acentra.Infrastructure";

    private readonly ITenantContext _tenant;
    private readonly TenantConnectionStrings _connections;

    public TenantDbContextFactory(ITenantContext tenant, TenantConnectionStrings connections)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(connections);

        _tenant = tenant;
        _connections = connections;
    }

    /// <inheritdoc />
    public AppDbContext Create()
    {
        // Fail closed. No resolved tenant means there is no database to route to and no TenantId to
        // filter on; returning a context over "all tenants" is never an option.
        if (!_tenant.IsResolved)
        {
            throw new InvalidOperationException(
                "Cannot create a tenant DbContext: no tenant is resolved. Tenant-scoped data access " +
                "without a resolved tenant is refused (fail closed).");
        }

        var scope = new ResolvedTenantContext(_tenant.TenantId, _tenant.Slug);

        return Build(scope, TenantDatabaseName.FromSlug(_tenant.Slug));
    }

    /// <inheritdoc />
    public AppDbContext CreateForTenant(TenantDescriptor tenant)
    {
        ArgumentNullException.ThrowIfNull(tenant);

        var scope = new ResolvedTenantContext(tenant.Id, tenant.Slug);

        return Build(scope, TenantDatabaseName.Normalize(tenant.DatabaseName));
    }

    private AppDbContext Build(ITenantContext scope, string databaseName)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(
                _connections.ForDatabase(databaseName),
                npgsql => npgsql.MigrationsAssembly(MigrationsAssembly))
            .AddInterceptors(new TenantStampInterceptor(scope))
            .Options;

        return new AppDbContext(options, scope);
    }
}
