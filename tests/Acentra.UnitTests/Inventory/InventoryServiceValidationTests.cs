using Acentra.Domain.Abstractions;
using Acentra.Infrastructure.Inventory;
using Acentra.Infrastructure.TenantData;

namespace Acentra.UnitTests;

/// <summary>
/// REQ-005 §4 validation rules, asserted at the service boundary with no database at all.
///
/// The seams are deliberately arranged so that the *only* way a test passes is if validation runs
/// before any data access: <see cref="DatabaseMustNotBeTouched"/> throws if the service opens a
/// context, so a validation failure must surface as <see cref="ArgumentException"/> (validation won)
/// and a valid input must surface as the factory's <see cref="InvalidOperationException"/> (validation
/// was passed and the service went on to open a context).
/// </summary>
public sealed class InventoryServiceValidationTests
{
    private const string DatabaseTouched = "the database was touched";

    [Theory]
    [InlineData("")]                                        // required
    [InlineData("   ")]                                     // required (whitespace)
    [InlineData("A")]                                       // too short
    [InlineData("SKU 001")]                                 // space
    [InlineData("sku-001")]                                 // lowercase
    [InlineData("SKU_001")]                                 // underscore
    public async Task CreateProduct_RejectsAnInvalidSku_BeforeTouchingTheDatabase(string sku)
    {
        var service = new InventoryService(new DatabaseMustNotBeTouched());

        await Assert.ThrowsAsync<ArgumentException>(
            () => service.CreateProductAsync(new ProductInput(sku, "Widget", null, 1m, 0, true)));
    }

    [Fact]
    public async Task CreateProduct_RejectsASkuThatIsTooLong()
    {
        var service = new InventoryService(new DatabaseMustNotBeTouched());

        var tooLong = new string('A', 33);

        await Assert.ThrowsAsync<ArgumentException>(
            () => service.CreateProductAsync(new ProductInput(tooLong, "Widget", null, 1m, 0, true)));
    }

    [Theory]
    [InlineData("SKU-001")]      // the documented example
    [InlineData("AB")]           // shortest legal
    [InlineData("A-1")]
    [InlineData("0123456789012345678901234567890-")] // exactly 32
    public async Task CreateProduct_AcceptsTheBoundarySkuShapes(string sku)
    {
        var service = new InventoryService(new DatabaseMustNotBeTouched());

        // Passing validation means execution reaches the context factory, which is where this test's
        // fake fails on purpose.
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.CreateProductAsync(new ProductInput(sku, "Widget", null, 1m, 0, true)));

        Assert.Equal(DatabaseTouched, exception.Message);
    }

    [Fact]
    public async Task CreateProduct_RejectsAnEmptyName()
    {
        var service = new InventoryService(new DatabaseMustNotBeTouched());

        await Assert.ThrowsAsync<ArgumentException>(
            () => service.CreateProductAsync(new ProductInput("SKU-001", "   ", null, 1m, 0, true)));
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(-100)]
    public async Task CreateProduct_RejectsANegativeUnitPrice(decimal price)
    {
        var service = new InventoryService(new DatabaseMustNotBeTouched());

        await Assert.ThrowsAsync<ArgumentException>(
            () => service.CreateProductAsync(new ProductInput("SKU-001", "Widget", null, price, 0, true)));
    }

    [Fact]
    public async Task CreateProduct_RejectsANegativeReorderLevel()
    {
        var service = new InventoryService(new DatabaseMustNotBeTouched());

        await Assert.ThrowsAsync<ArgumentException>(
            () => service.CreateProductAsync(new ProductInput("SKU-001", "Widget", null, 1m, -1, true)));
    }

    [Fact]
    public async Task CreateProduct_AcceptsAZeroPriceAndZeroReorderLevel()
    {
        var service = new InventoryService(new DatabaseMustNotBeTouched());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.CreateProductAsync(new ProductInput("SKU-001", "Widget", null, 0m, 0, true)));

        Assert.Equal(DatabaseTouched, exception.Message);
    }

    [Fact]
    public async Task UpdateProduct_RejectsAnInvalidSku_BeforeTouchingTheDatabase()
    {
        var service = new InventoryService(new DatabaseMustNotBeTouched());

        await Assert.ThrowsAsync<ArgumentException>(
            () => service.UpdateProductAsync(Guid.NewGuid(), new ProductInput("nope!", "Widget", null, 1m, 0, true)));
    }

    [Fact]
    public async Task AdjustStock_RejectsAZeroDelta_BeforeTouchingTheDatabase()
    {
        var service = new InventoryService(new DatabaseMustNotBeTouched());

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => service.AdjustStockAsync(Guid.NewGuid(), 0, "received", null));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task AdjustStock_RejectsAnEmptyReason(string reason)
    {
        var service = new InventoryService(new DatabaseMustNotBeTouched());

        await Assert.ThrowsAsync<ArgumentException>(
            () => service.AdjustStockAsync(Guid.NewGuid(), 1, reason, null));
    }

    [Fact]
    public async Task AdjustStock_RejectsAnOverlongReason()
    {
        var service = new InventoryService(new DatabaseMustNotBeTouched());

        var reason = new string('r', InventoryService.ReasonMaxLength + 1);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => service.AdjustStockAsync(Guid.NewGuid(), 1, reason, null));
    }

    [Fact]
    public async Task AdjustStock_WithValidInput_ReachesTheDatabaseBoundary()
    {
        var service = new InventoryService(new DatabaseMustNotBeTouched());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.AdjustStockAsync(Guid.NewGuid(), -3, "shrinkage", "counted twice"));

        Assert.Equal(DatabaseTouched, exception.Message);
    }

    // --------------------------------------------------- fail closed without a tenant

    /// <summary>
    /// Every method refuses to work without a resolved tenant — the service can never fall back to
    /// "all tenants" or to a previous tenant's connection. This is what makes an inventory page's
    /// "select a tenant" state the only thing that can be rendered.
    /// </summary>
    [Fact]
    public async Task EveryMethod_FailsClosed_WhenNoTenantIsResolved()
    {
        // The real factory, over a tenant-less context: nothing here is faked.
        var factory = new TenantDbContextFactory(
            new UnresolvedTenant(),
            new TenantConnectionStrings("Host=localhost;Database={0};Username=none"));

        var service = new InventoryService(factory);
        var input = new ProductInput("SKU-001", "Widget", null, 1m, 0, true);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ListProductsAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetProductAsync(Guid.NewGuid()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateProductAsync(input));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.UpdateProductAsync(Guid.NewGuid(), input));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DeleteProductAsync(Guid.NewGuid()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.AdjustStockAsync(Guid.NewGuid(), 1, "received", null));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ListMovementsAsync(Guid.NewGuid()));
    }

    private sealed class DatabaseMustNotBeTouched : ITenantDbContextFactory
    {
        public AppDbContext Create() => throw new InvalidOperationException(DatabaseTouched);

        public AppDbContext CreateForTenant(TenantDescriptor tenant) => throw new InvalidOperationException(DatabaseTouched);
    }

    private sealed class UnresolvedTenant : ITenantContext
    {
        public Guid TenantId => Guid.Empty;

        public string Slug => string.Empty;

        public bool IsResolved => false;
    }
}
