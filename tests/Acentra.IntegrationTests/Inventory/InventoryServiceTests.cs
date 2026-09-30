using Acentra.Domain.Abstractions;
using Acentra.Domain.Entities;
using Acentra.Infrastructure.Inventory;
using Acentra.Infrastructure.TenantData;
using Acentra.Web.Auth;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Acentra.IntegrationTests;

/// <summary>
/// REQ-005 §4/§6 service proofs against real PostgreSQL, on two dedicated databases provisioned by
/// the real <see cref="TenantProvisioner"/> (so the suite never touches the dev tenants):
///
/// <list type="number">
///   <item>two tenants may own the same SKU, and each sees only its own row;</item>
///   <item>reading, updating, deleting or adjusting another tenant's product is "not found" — never a
///         cross-tenant read or write;</item>
///   <item>a stock adjustment writes the <c>StockMovement</c> and updates the <c>StockLevel</c>
///         together, and the audit trail is refused deletion;</item>
///   <item>a duplicate SKU is a friendly message, not an EF exception;</item>
///   <item>validation happens before any context is opened.</item>
/// </list>
/// </summary>
public sealed class InventoryServiceTests
{
    private static readonly Guid TenantAId = Guid.Parse("e0000000-0000-4000-8000-00000000eeee");
    private static readonly Guid TenantBId = Guid.Parse("f0000000-0000-4000-8000-00000000ffff");

    /// <summary>
    /// Both tenants deliberately share ONE database, exactly as <c>TenantFileUniquenessTests</c> does:
    /// the isolation under test is then the tenant query filter (and the tenant-scoped unique index)
    /// rather than the physical database split, which is the stronger assertion — and it keeps the
    /// suite to a single extra database.
    /// </summary>
    public const string SharedDatabase = "acentra_req005";

    private static readonly TenantDescriptor TenantA =
        new(TenantAId, "req005-a", "REQ-005 Tenant A", SharedDatabase);

    private static readonly TenantDescriptor TenantB =
        new(TenantBId, "req005-b", "REQ-005 Tenant B", SharedDatabase);

    private static readonly TenantConnectionStrings Connections =
        TenantConnectionStrings.FromConfiguration(LoadConfiguration());

    // ------------------------------------------------------ 1. same SKU, two tenants

    [Fact]
    public async Task BothTenantsMayOwnTheSameSku_AndEachSeesOnlyItsOwnRow()
    {
        await ProvisionAsync(TenantB);

        var serviceA = ServiceFor(TenantA);
        var serviceB = ServiceFor(TenantB);

        // A fresh SKU per run so the suite is re-runnable; the *same* value in both tenants is the
        // point (the unique index is tenant-scoped, not global).
        var sku = NewSku("shared");

        var productA = await serviceA.CreateProductAsync(new ProductInput(sku, "Tenant A widget", null, 10m, 1, true));
        var productB = await serviceB.CreateProductAsync(new ProductInput(sku, "Tenant B widget", null, 20m, 2, true));

        Assert.NotEqual(productA.Id, productB.Id);
        Assert.Equal(TenantAId, productA.TenantId);
        Assert.Equal(TenantBId, productB.TenantId);
        Assert.Equal(sku, productA.Sku);
        Assert.Equal(sku, productB.Sku);

        var listA = await serviceA.ListProductsAsync();
        var rowA = Assert.Single(listA, product => product.Sku == sku);
        Assert.Equal(productA.Id, rowA.Id);
        Assert.Equal("Tenant A widget", rowA.Name);
        Assert.All(listA, product => Assert.Equal(TenantAId, product.TenantId));

        var listB = await serviceB.ListProductsAsync();
        var rowB = Assert.Single(listB, product => product.Sku == sku);
        Assert.Equal(productB.Id, rowB.Id);
        Assert.Equal("Tenant B widget", rowB.Name);
        Assert.All(listB, product => Assert.Equal(TenantBId, product.TenantId));

        // …and neither can address the other's row, even though the SKU is identical.
        Assert.Null(await serviceA.GetProductAsync(productB.Id));
        Assert.Null(await serviceB.GetProductAsync(productA.Id));
    }

    // ------------------------------------------- 2. a foreign row is simply not found

    [Fact]
    public async Task Delete_Read_UpdateAndAdjust_OfAnotherTenantsProduct_AreNotFound()
    {
        await ProvisionAsync(TenantA);
        await ProvisionAsync(TenantB);

        var serviceA = ServiceFor(TenantA);
        var serviceB = ServiceFor(TenantB);

        var sku = NewSku("foreign");
        var productA = await serviceA.CreateProductAsync(new ProductInput(sku, "Tenant A widget", null, 5m, 0, true));

        // Acting as B: A's product id is unknown.
        Assert.Null(await serviceB.GetProductAsync(productA.Id));
        Assert.DoesNotContain(await serviceB.ListProductsAsync(), product => product.Id == productA.Id);

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => serviceB.UpdateProductAsync(productA.Id, new ProductInput(sku, "hijacked", null, 1m, 0, true)));

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => serviceB.DeleteProductAsync(productA.Id));

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => serviceB.AdjustStockAsync(productA.Id, 5, "hijack", null));

        // Nothing was read, written or adjusted across the boundary.
        var stillThere = await serviceA.GetProductAsync(productA.Id);
        Assert.NotNull(stillThere);
        Assert.Equal("Tenant A widget", stillThere!.Name);
        Assert.Equal(5m, stillThere.UnitPrice);

        Assert.Empty(await serviceB.ListMovementsAsync(productA.Id));
        Assert.Empty(await serviceA.ListMovementsAsync(productA.Id));
    }

    [Fact]
    public async Task Delete_OfAMissingId_IsNotFound()
    {
        await ProvisionAsync(TenantA);

        var service = ServiceFor(TenantA);

        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.DeleteProductAsync(Guid.NewGuid()));
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => service.AdjustStockAsync(Guid.NewGuid(), 1, "received", null));
    }

    // ------------------------------------------------------------- 3. stock movements

    [Fact]
    public async Task AdjustStock_WritesTheMovementAndUpdatesTheLevel_Together()
    {
        await ProvisionAsync(TenantA);

        var service = ServiceFor(TenantA);
        var sku = NewSku("stock");
        var product = await service.CreateProductAsync(new ProductInput(sku, "Stocked widget", null, 3m, 0, true));

        // A newly created product starts at zero, so the first adjustment lands on exactly its delta.
        var afterFirst = await service.AdjustStockAsync(product.Id, 5, "goods received", "PO-1");
        Assert.Equal(5, afterFirst.Quantity);
        Assert.Equal(TenantAId, afterFirst.TenantId);
        Assert.Equal(product.Id, afterFirst.ProductId);

        var afterSecond = await service.AdjustStockAsync(product.Id, -2, "sale", null);
        Assert.Equal(3, afterSecond.Quantity);

        var movements = await service.ListMovementsAsync(product.Id);
        Assert.Equal(2, movements.Count);

        // Newest first.
        Assert.Equal(-2, movements[0].Delta);
        Assert.Equal("sale", movements[0].Reason);
        Assert.Equal(5, movements[1].Delta);
        Assert.Equal("goods received", movements[1].Reason);
        Assert.Equal("PO-1", movements[1].Note);
        Assert.All(movements, movement =>
        {
            Assert.Equal(TenantAId, movement.TenantId);
            Assert.Equal(product.Id, movement.ProductId);
        });

        // The level and the audit trail agree — the invariant a single SaveChanges gives us.
        Assert.Equal(afterSecond.Quantity, movements.Sum(movement => movement.Delta));

        // And both rows are really in this tenant's database (one transaction, one commit).
        await using var db = CreateFor(TenantA);
        var storedLevel = await db.StockLevels.SingleAsync(level => level.ProductId == product.Id);
        Assert.Equal(3, storedLevel.Quantity);
        Assert.Equal(TenantAId, storedLevel.TenantId);
        Assert.Equal(2, await db.StockMovements.CountAsync(movement => movement.ProductId == product.Id));
    }

    [Fact]
    public async Task Delete_OfAProductWithMovements_IsRefused_AndNothingIsRemoved()
    {
        await ProvisionAsync(TenantA);

        var service = ServiceFor(TenantA);
        var product = await service.CreateProductAsync(new ProductInput(NewSku("audit"), "Audited widget", null, 1m, 0, true));

        await service.AdjustStockAsync(product.Id, 4, "received", null);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.DeleteProductAsync(product.Id));

        Assert.Contains("stock movements", exception.Message, StringComparison.OrdinalIgnoreCase);

        Assert.NotNull(await service.GetProductAsync(product.Id));
        Assert.Single(await service.ListMovementsAsync(product.Id));
    }

    [Fact]
    public async Task Delete_OfAProductWithoutMovements_RemovesTheProductAndItsLevel()
    {
        await ProvisionAsync(TenantA);

        var service = ServiceFor(TenantA);
        var product = await service.CreateProductAsync(new ProductInput(NewSku("delete"), "Disposable widget", null, 1m, 0, true));

        await service.DeleteProductAsync(product.Id);

        Assert.Null(await service.GetProductAsync(product.Id));
        Assert.DoesNotContain(await service.ListProductsAsync(), candidate => candidate.Id == product.Id);

        await using var db = CreateFor(TenantA);
        Assert.False(await db.StockLevels.AnyAsync(level => level.ProductId == product.Id));
    }

    // ------------------------------------------------------------- 4. create / update

    [Fact]
    public async Task CreateAndUpdate_RoundTripAndStampTheTenant()
    {
        await ProvisionAsync(TenantA);

        var service = ServiceFor(TenantA);
        var sku = NewSku("crud");

        var created = await service.CreateProductAsync(
            new ProductInput(sku, "  Padded name  ", "   ", 12.5m, 3, true));

        Assert.Equal("Padded name", created.Name);     // trimmed
        Assert.Null(created.Description);              // whitespace becomes null
        Assert.Equal(12.5m, created.UnitPrice);
        Assert.Equal(TenantAId, created.TenantId);
        Assert.NotEqual(default, created.CreatedUtc);

        var updated = await service.UpdateProductAsync(
            created.Id,
            new ProductInput(sku, "Renamed", "Now with a description", 0m, 0, false));

        Assert.Equal(created.Id, updated.Id);
        Assert.Equal("Renamed", updated.Name);
        Assert.Equal("Now with a description", updated.Description);
        Assert.Equal(0m, updated.UnitPrice);
        Assert.False(updated.IsActive);

        var reread = await service.GetProductAsync(created.Id);
        Assert.NotNull(reread);
        Assert.Equal("Renamed", reread!.Name);
        Assert.False(reread.IsActive);
    }

    [Fact]
    public async Task Create_OfADuplicateSku_IsAFriendlyValidationMessage_NotAnEfException()
    {
        await ProvisionAsync(TenantA);

        var service = ServiceFor(TenantA);
        var sku = NewSku("dupe");

        await service.CreateProductAsync(new ProductInput(sku, "First", null, 1m, 0, true));

        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => service.CreateProductAsync(new ProductInput(sku, "Second", null, 1m, 0, true)));

        Assert.Contains(sku, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("DbUpdateException", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Update_ToASkuAnotherProductUses_IsRejected_ButKeepingItsOwnIsFine()
    {
        await ProvisionAsync(TenantA);

        var service = ServiceFor(TenantA);
        var firstSku = NewSku("keep");
        var secondSku = NewSku("other");

        var first = await service.CreateProductAsync(new ProductInput(firstSku, "First", null, 1m, 0, true));
        var second = await service.CreateProductAsync(new ProductInput(secondSku, "Second", null, 1m, 0, true));

        await Assert.ThrowsAsync<ArgumentException>(
            () => service.UpdateProductAsync(second.Id, new ProductInput(firstSku, "Second", null, 1m, 0, true)));

        // Re-saving a product with its own SKU is not a duplicate.
        var saved = await service.UpdateProductAsync(second.Id, new ProductInput(secondSku, "Second renamed", null, 1m, 0, true));
        Assert.Equal("Second renamed", saved.Name);

        // …and the first one is untouched.
        var reread = await service.GetProductAsync(first.Id);
        Assert.Equal("First", reread!.Name);
    }

    // ----------------------------------------------------------------------- helpers

    private static InventoryService ServiceFor(TenantDescriptor tenant) =>
        new(new SharedDatabaseFactory(tenant));

    /// <summary>
    /// A factory whose ambient (slug-derived) path points at the tenant's declared database instead of
    /// <c>acentra_{slug}</c>, so both test tenants live in one database. Everything else — the context,
    /// the query filter, the write interceptor — is the production implementation.
    /// </summary>
    private sealed class SharedDatabaseFactory(TenantDescriptor tenant) : ITenantDbContextFactory
    {
        private readonly TenantDbContextFactory _inner =
            new(new ResolvedTenantContext(tenant.Id, tenant.Slug), Connections);

        public AppDbContext Create() => _inner.CreateForTenant(tenant);

        public AppDbContext CreateForTenant(TenantDescriptor other) => _inner.CreateForTenant(other);
    }

    private static AppDbContext CreateFor(TenantDescriptor tenant) =>
        new TenantDbContextFactory(new ResolvedTenantContext(tenant.Id, tenant.Slug), Connections)
            .CreateForTenant(tenant);

    private static async Task ProvisionAsync(TenantDescriptor tenant)
    {
        var provisioner = new TenantProvisioner(
            Connections,
            new TenantDbContextFactory(new TenantState(), Connections),
            NullLogger<TenantProvisioner>.Instance);

        await provisioner.ProvisionAsync(tenant, CancellationToken.None);
    }

    private static string NewSku(string label) =>
        $"SKU-{label}-{Guid.NewGuid().ToString("N")[..8]}".ToUpperInvariant();

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
