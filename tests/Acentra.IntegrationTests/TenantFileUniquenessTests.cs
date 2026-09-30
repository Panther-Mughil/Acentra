using Acentra.Domain.Abstractions;
using Acentra.Domain.Entities;
using Acentra.Infrastructure.ControlPlane;
using Acentra.Infrastructure.TenantData;
using Acentra.Web.Auth;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Acentra.IntegrationTests;

/// <summary>
/// REQ-004 §6: <c>TenantFile</c> rows are unique per <c>(TenantId, Key)</c> — and, just as
/// importantly, the index is <em>tenant-scoped</em> rather than global, so the identical key stays
/// legal for a different tenant. The second half is what proves multi-tenancy survives the
/// constraint; a global unique index would satisfy only the first.
///
/// Driven against real PostgreSQL on a dedicated database (<c>acentra_req004_files</c>) migrated by
/// the real <see cref="TenantProvisioner"/>, so the new <c>AddTenantFileIndexes</c> migration is
/// exercised rather than assumed.
/// </summary>
public sealed class TenantFileUniquenessTests
{
    private static readonly Guid TenantAId = Guid.Parse("c0000000-0000-4000-8000-00000000cccc");
    private static readonly Guid TenantBId = Guid.Parse("d0000000-0000-4000-8000-00000000dddd");

    private static readonly TenantDescriptor TenantA =
        new(TenantAId, "req004-a", "REQ-004 Tenant A", "acentra_req004_files");

    private static readonly TenantDescriptor TenantB =
        new(TenantBId, "req004-b", "REQ-004 Tenant B", "acentra_req004_files");

    private static readonly TenantConnectionStrings Connections =
        TenantConnectionStrings.FromConfiguration(LoadConfiguration());

    [Fact]
    public async Task Files_AreUniquePerTenantAndKey_ButTheSameKeyIsAllowedForAnotherTenant()
    {
        await ProvisionAsync(TenantA);

        // A fresh key per run keeps the test re-runnable without truncating the table.
        var key = $"tenants/{TenantAId:N}/documents/{Guid.NewGuid():N}.pdf";

        await using (var seed = CreateFor(TenantA))
        {
            seed.Files.Add(NewFile(TenantAId, key, "quarterly.pdf"));
            Assert.Equal(1, await seed.SaveChangesAsync());
        }

        // 1. A duplicate (TenantId, Key) for the SAME tenant must be refused by the unique index.
        await using (var duplicate = CreateFor(TenantA))
        {
            duplicate.Files.Add(NewFile(TenantAId, key, "quarterly-copy.pdf"));

            var exception = await Assert.ThrowsAsync<DbUpdateException>(() => duplicate.SaveChangesAsync());
            var postgres = Assert.IsType<PostgresException>(exception.InnerException);

            Assert.Equal(PostgresErrorCodes.UniqueViolation, postgres.SqlState);
            Assert.Equal("ux_tenant_files_tenant_key", postgres.ConstraintName);
        }

        // 2. The SAME key under a DIFFERENT tenant must be accepted — the index is tenant-scoped,
        //    not global. A global unique index would (wrongly) reject this.
        await using (var otherTenant = BuildContextAgainstDatabase(TenantA.DatabaseName, TenantB))
        {
            otherTenant.Files.Add(NewFile(TenantBId, key, "quarterly.pdf"));
            Assert.Equal(1, await otherTenant.SaveChangesAsync());
        }

        // Both rows coexist; each tenant sees only its own through the global query filter.
        await using (var dbA = CreateFor(TenantA))
        {
            Assert.Equal(1, await dbA.Files.CountAsync(f => f.Key == key));
            Assert.All(await dbA.Files.Where(f => f.Key == key).ToListAsync(), f => Assert.Equal(TenantAId, f.TenantId));
        }

        await using (var dbB = BuildContextAgainstDatabase(TenantA.DatabaseName, TenantB))
        {
            Assert.Equal(1, await dbB.Files.CountAsync(f => f.Key == key));
            Assert.All(await dbB.Files.Where(f => f.Key == key).ToListAsync(), f => Assert.Equal(TenantBId, f.TenantId));
        }
    }

    // --------------------------------------------------------------------------- helpers

    private static TenantFile NewFile(Guid tenantId, string key, string fileName) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = tenantId,
        Key = key,
        FileName = fileName,
        ContentType = "application/pdf",
        SizeBytes = 2048,
        UploadedUtc = DateTime.UtcNow,
    };

    private static AppDbContext CreateFor(TenantDescriptor tenant) =>
        new TenantDbContextFactory(new ResolvedTenantContext(tenant.Id, tenant.Slug), Connections)
            .CreateForTenant(tenant);

    /// <summary>
    /// Expresses "the connection and the tenant disagree", which is the only way to place a second
    /// tenant's row into the same physical database — exactly the situation the tenant-scoped index
    /// has to tolerate.
    /// </summary>
    private static AppDbContext BuildContextAgainstDatabase(string databaseName, TenantDescriptor tenant)
    {
        var scope = new ResolvedTenantContext(tenant.Id, tenant.Slug);

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(Connections.ForDatabase(databaseName))
            .AddInterceptors(new TenantStampInterceptor(scope))
            .Options;

        return new AppDbContext(options, scope);
    }

    private static async Task ProvisionAsync(TenantDescriptor tenant)
    {
        var provisioner = new TenantProvisioner(
            Connections,
            new TenantDbContextFactory(new TenantState(), Connections),
            NullLogger<TenantProvisioner>.Instance);

        await provisioner.ProvisionAsync(tenant, CancellationToken.None);
    }

    private static IConfiguration LoadConfiguration()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Acentra.slnx")))
        {
            directory = directory.Parent;
        }

        var root = directory?.FullName
            ?? throw new InvalidOperationException("Could not locate the repository root (Acentra.slnx).");

        return new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(root, "src", "Acentra.Web", "appsettings.Development.json"), optional: false)
            .AddJsonFile(Path.Combine(root, "src", "Acentra.Web", "appsettings.json"), optional: false)
            .AddEnvironmentVariables()
            .Build();
    }
}
