using System.Text.RegularExpressions;
using Acentra.Domain.Abstractions;
using Acentra.Domain.Entities;
using Acentra.Infrastructure.TenantData;
using Microsoft.EntityFrameworkCore;

namespace Acentra.IntegrationTests;

/// <summary>
/// Provisions two throwaway tenant databases (GUID-suffixed) once per class and drops them in
/// teardown, exactly as the committed <c>TenantDataIsolationTests</c> does. The developer's real
/// tenant databases are never touched; a crashed run leaves only <c>acentra_req006_*</c> databases,
/// which the next run never reuses.
/// </summary>
public sealed class IsolationMatrixFixture : IAsyncLifetime
{
    public IsolationMatrixFixture()
    {
        TenantA = ThrowawayTenant.Create("matrixa");
        TenantB = ThrowawayTenant.Create("matrixb");
    }

    public ThrowawayTenant TenantA { get; }

    public ThrowawayTenant TenantB { get; }

    public async Task InitializeAsync()
    {
        await TenantA.ProvisionAsync();
        await TenantB.ProvisionAsync();
    }

    public async Task DisposeAsync()
    {
        await TenantA.DropAsync();
        await TenantB.DropAsync();
    }
}

/// <summary>
/// REQ-006 §4 boundaries B1, B2, B3, B7 and B8 — the data-layer half of the isolation matrix.
///
/// The plan writes these against <c>PUT /api/products/{id}</c>, but this application has no REST
/// API (it is Blazor Server plus a service layer). They are therefore driven through the committed
/// <c>ITenantDbContextFactory</c> / <c>AppDbContext</c> — the same seam the service layer uses — so
/// they depend only on REQ-003/REQ-004 and never on the in-flight <c>IInventoryService</c>.
///
/// The assertions are deliberately filter- and interceptor-sensitive: they fail if a global query
/// filter or the write interceptor is removed (see the mutation evidence in the REQ-006 report).
/// </summary>
public sealed class IsolationMatrixTests : IClassFixture<IsolationMatrixFixture>
{
    private readonly IsolationMatrixFixture _fixture;

    public IsolationMatrixTests(IsolationMatrixFixture fixture) => _fixture = fixture;

    private TenantDescriptor TenantA => _fixture.TenantA.Descriptor;

    private TenantDescriptor TenantB => _fixture.TenantB.Descriptor;

    // ------------------------------------------------------------------------------- B1

    [Fact]
    public async Task B1_ReadIsolation_ReturnsOnlyTheCurrentTenantsRows_AndNeverEmpty()
    {
        var skuA = NewSku("b1a");
        var skuB = NewSku("b1b");
        var idA = Guid.NewGuid();
        var idB = Guid.NewGuid();

        await using (var dbA = IsolationTestSupport.CreateFor(TenantA))
        {
            dbA.Products.Add(NewProduct(skuA, idA));
            await dbA.SaveChangesAsync();
        }

        // Simulate a connection-routing bug: a row carrying tenant B's TenantId is physically
        // written into tenant A's database. Only the global query filter stands between it and a read.
        await using (var forced = IsolationTestSupport.CreateForTenantAgainstDatabase(TenantB, TenantA.DatabaseName))
        {
            forced.Products.Add(NewProduct(skuB, idB, TenantB.Id));
            await forced.SaveChangesAsync();
        }

        await using (var dbA = IsolationTestSupport.CreateFor(TenantA))
        {
            var visible = await dbA.Products
                .Where(p => p.Sku == skuA || p.Sku == skuB)
                .ToListAsync();

            // Positive row count: the test must not pass because the query returned nothing.
            var only = Assert.Single(visible);
            Assert.Equal(skuA, only.Sku);
            Assert.All(visible, p => Assert.Equal(TenantA.Id, p.TenantId));

            // Tenant B's co-located row is not addressable at all, not merely left out of a list.
            Assert.False(await dbA.Products.AnyAsync(p => p.Id == idB));
        }
    }

    // ------------------------------------------------------------------------------- B2

    [Fact]
    public async Task B2_CrossTenantWriteById_IsNotAddressable_AndCannotBeSmuggled_AndLeavesTheRowUnchanged()
    {
        var skuA = NewSku("b2a");
        var skuB = NewSku("b2b");
        var idA = Guid.NewGuid();
        var idB = Guid.NewGuid();

        await using (var dbA = IsolationService('A'))
        {
            dbA.Products.Add(NewProduct(skuA, idA));
            await dbA.SaveChangesAsync();
        }

        await using (var dbB = IsolationService('B'))
        {
            dbB.Products.Add(NewProduct(skuB, idB));
            await dbB.SaveChangesAsync();
        }

        // 1. "PUT a product with B's id while acting as A": tenant B's id is not addressable through
        //    A's context — the 404-equivalent — so no cross-tenant write can even be attempted.
        await using (var dbA = IsolationService('A'))
        {
            Assert.Null(await dbA.Products.SingleOrDefaultAsync(p => p.Id == idB));
        }

        // 2. Smuggling B's row in as a tracked entity still cannot write it while acting as A.
        await using (var dbA = IsolationService('A'))
        {
            dbA.Products.Update(NewProduct(skuB, idB, TenantB.Id));

            await Assert.ThrowsAsync<TenantIsolationException>(() => dbA.SaveChangesAsync());
        }

        // 3. Tenant B's row is untouched.
        await using (var dbB = IsolationService('B'))
        {
            var stored = await dbB.Products.SingleAsync(p => p.Id == idB);
            Assert.Equal(skuB, stored.Sku);
            Assert.Equal(TenantB.Id, stored.TenantId);
        }

        // 4. Positive control: the test is not vacuous — A can still update its OWN row.
        await using (var dbA = IsolationService('A'))
        {
            var own = await dbA.Products.SingleAsync(p => p.Id == idA);
            own.Name = "Updated by the owning tenant";
            Assert.Equal(1, await dbA.SaveChangesAsync());
        }
    }

    // ------------------------------------------------------------------------------- B3

    [Fact]
    public async Task B3_CrossTenantDelete_IsRefused_AndLeavesTheOtherTenantsRowPresent()
    {
        var skuA = NewSku("b3a");
        var skuB = NewSku("b3b");
        var idA = Guid.NewGuid();
        var idB = Guid.NewGuid();

        await using (var dbA = IsolationService('A'))
        {
            dbA.Products.Add(NewProduct(skuA, idA));
            await dbA.SaveChangesAsync();
        }

        await using (var dbB = IsolationService('B'))
        {
            dbB.Products.Add(NewProduct(skuB, idB));
            await dbB.SaveChangesAsync();
        }

        // Acting as A, tenant B's id is not addressable — there is nothing to delete (404-equivalent).
        await using (var dbA = IsolationService('A'))
        {
            Assert.Null(await dbA.Products.SingleOrDefaultAsync(p => p.Id == idB));
        }

        // A direct attempt to delete a row that carries B's TenantId is refused by the interceptor.
        await using (var dbA = IsolationService('A'))
        {
            dbA.Products.Remove(NewProduct(skuB, idB, TenantB.Id));

            await Assert.ThrowsAsync<TenantIsolationException>(() => dbA.SaveChangesAsync());
        }

        // B's row is still present.
        await using (var dbB = IsolationService('B'))
        {
            Assert.Equal(1, await dbB.Products.CountAsync(p => p.Id == idB));
        }

        // Positive control: A can delete its OWN row.
        await using (var dbA = IsolationService('A'))
        {
            var own = await dbA.Products.SingleAsync(p => p.Id == idA);
            dbA.Products.Remove(own);
            Assert.Equal(1, await dbA.SaveChangesAsync());
        }
    }

    // ------------------------------------------------------------------------------- B7

    [Fact]
    public async Task B7_WritePathStamping_InsertWithEmptyTenantId_IsStampedFromTheResolvedTenant()
    {
        var sku = NewSku("b7stamp");
        var id = Guid.NewGuid();

        await using (var dbA = IsolationService('A'))
        {
            // TenantId deliberately left at Guid.Empty: the client never supplies it.
            dbA.Products.Add(NewProduct(sku, id, Guid.Empty));
            Assert.Equal(1, await dbA.SaveChangesAsync());
        }

        await using (var dbA = IsolationService('A'))
        {
            var stored = await dbA.Products.SingleAsync(p => p.Id == id);

            Assert.Equal(TenantA.Id, stored.TenantId);
            Assert.NotEqual(Guid.Empty, stored.TenantId);
        }
    }

    [Fact]
    public async Task B7_WritePathStamping_InsertCarryingForeignTenantId_Throws_AndWritesNothing()
    {
        var sku = NewSku("b7foreign");

        await using (var dbA = IsolationService('A'))
        {
            dbA.Products.Add(NewProduct(sku, Guid.NewGuid(), TenantB.Id));

            var exception = await Assert.ThrowsAsync<TenantIsolationException>(() => dbA.SaveChangesAsync());

            Assert.Contains("Refusing to insert", exception.Message, StringComparison.Ordinal);
            Assert.Contains(TenantB.Id.ToString(), exception.Message, StringComparison.Ordinal);
        }

        await using (var dbA = IsolationService('A'))
        {
            Assert.False(await dbA.Products.AnyAsync(p => p.Sku == sku));
        }
    }

    // ------------------------------------------------------------------------------- B8

    [Fact]
    public async Task B8_FilterAndPhysicalSplit_ForcedForeignTenantSeesOnlyItsOwnRows_InTheSameDatabase()
    {
        var skuA = NewSku("b8a");
        var skuB = NewSku("b8b");
        var skuBRouting = NewSku("b8route");
        var idA = Guid.NewGuid();
        var idB = Guid.NewGuid();
        var idBRouting = Guid.NewGuid();

        await using (var dbA = IsolationService('A'))
        {
            dbA.Products.Add(NewProduct(skuA, idA));
            await dbA.SaveChangesAsync();
        }

        await using (var dbB = IsolationService('B'))
        {
            dbB.Products.Add(NewProduct(skuB, idB));
            await dbB.SaveChangesAsync();
        }

        // The physical split: A's context talks to A's database and cannot see B's row at all.
        await using (var dbA = IsolationService('A'))
        {
            Assert.Equal(TenantDatabaseName.Normalize(TenantA.DatabaseName), dbA.Database.GetDbConnection().Database);
            Assert.Equal(1, await dbA.Products.CountAsync(p => p.Id == idA));
            Assert.False(await dbA.Products.AnyAsync(p => p.Id == idB));
        }

        // Force a foreign row into A's physical database. Now the filter, not the connection, is
        // the only thing separating the tenants.
        await using (var forced = IsolationTestSupport.CreateForTenantAgainstDatabase(TenantB, TenantA.DatabaseName))
        {
            forced.Products.Add(NewProduct(skuBRouting, idBRouting, TenantB.Id));
            await forced.SaveChangesAsync();
        }

        await using (var dbA = IsolationService('A'))
        {
            var seen = await dbA.Products
                .Where(p => p.Id == idA || p.Id == idB || p.Id == idBRouting)
                .Select(p => p.Id)
                .ToListAsync();

            Assert.Equal([idA], seen); // positive, and only A's row
        }

        // The forced tenant sees its own co-located row — proving the filter reads the executing
        // context's tenant, not the cached model or the database the connection points at.
        await using (var forced = IsolationTestSupport.CreateForTenantAgainstDatabase(TenantB, TenantA.DatabaseName))
        {
            var seen = await forced.Products
                .Where(p => p.Id == idA || p.Id == idB || p.Id == idBRouting)
                .Select(p => p.Id)
                .ToListAsync();

            Assert.Equal([idBRouting], seen); // positive, and only B's row
        }
    }

    // --------------------------------------------------------------------------- helpers

    private AppDbContext IsolationService(char which) => which switch
    {
        'A' => IsolationTestSupport.CreateFor(TenantA),
        'B' => IsolationTestSupport.CreateFor(TenantB),
        _ => throw new ArgumentOutOfRangeException(nameof(which))
    };

    private static Product NewProduct(string sku, Guid id, Guid tenantId = default) => new()
    {
        Id = id,
        TenantId = tenantId,
        Sku = sku,
        Name = $"Product {sku}",
        Description = "REQ-006 isolation matrix row",
        UnitPrice = 12.34m,
        ReorderLevel = 5,
        IsActive = true,
        CreatedUtc = DateTime.UtcNow,
        UpdatedUtc = DateTime.UtcNow
    };

    private static string NewSku(string label) => $"SKU-{label}-{Guid.NewGuid():N}";
}

/// <summary>
/// REQ-006 §4 boundary B10 — a static scan proving no production call site of
/// <c>IgnoreQueryFilters()</c> exists. <c>IgnoreQueryFilters</c> is the classical way to defeat the
/// global query filters, so the architecture audit (§5.6) requires every call site to be accounted
/// for. The scan asserts a positive number of files so it cannot pass by seeing nothing.
/// </summary>
public sealed class IgnoreQueryFiltersAuditTests
{
    private static readonly Regex CallSite = new(@"\.\s*IgnoreQueryFilters\s*\(", RegexOptions.Compiled);

    [Fact]
    public void B10_NoProductionCallSiteUsesIgnoreQueryFilters()
    {
        var root = IsolationTestSupport.RepositoryRoot();
        var sourceRoot = Path.Combine(root, "src");
        Assert.True(Directory.Exists(sourceRoot), $"Expected a production source tree at '{sourceRoot}'.");

        var files = Directory
            .EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsBuildOutput(path))
            .ToList();

        // Positive guard: an empty scan must never look like success.
        Assert.NotEmpty(files);
        Assert.Contains(files, path => Path.GetFileName(path) == "AppDbContext.cs");
        Assert.Contains(files, path => Path.GetFileName(path) == "TenantResolutionMiddleware.cs");

        var offenders = new List<string>();

        foreach (var file in files)
        {
            var lines = File.ReadAllLines(file);

            for (var index = 0; index < lines.Length; index++)
            {
                if (CallSite.IsMatch(lines[index]))
                {
                    offenders.Add($"{Path.GetRelativePath(root, file)}:{index + 1}: {lines[index].Trim()}");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "IgnoreQueryFilters() call site(s) found in production code (query filters must not be " +
            "defeated outside an audited admin path):\n" + string.Join("\n", offenders));
    }

    private static bool IsBuildOutput(string path)
    {
        var separator = Path.DirectorySeparatorChar;

        return path.Contains($"{separator}bin{separator}", StringComparison.Ordinal) ||
               path.Contains($"{separator}obj{separator}", StringComparison.Ordinal);
    }
}
