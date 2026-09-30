using Acentra.Domain.Entities;
using Acentra.Infrastructure.Inventory;
using Acentra.Web.Auth;
using Acentra.Web.Components.Pages.Inventory;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Acentra.IntegrationTests;

/// <summary>
/// REQ-005 §6 — "Unresolved tenant → 'select a tenant' state, no data and no exception" — rendered
/// for real (the page component, not a copy of its logic) with no tenant resolved.
///
/// A tenant-scoped URL cannot reach the page without a tenant (the REQ-008 middleware fails closed
/// with 400/403 first), so the state that matters is the in-circuit one: REQ-009 clears
/// <c>TenantState</c> while a user sits on an inventory page. This renders the page in exactly that
/// state and asserts the notice appears, the service is never called and nothing throws.
/// </summary>
public sealed class InventoryPageUnresolvedTenantTests
{
    [Fact]
    public async Task ProductsPage_WithNoResolvedTenant_RendersTheNoticeAndNoRows()
    {
        var tenant = new TenantState();
        var inventory = new RecordingInventoryService();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TenantState>(tenant);
        services.AddSingleton<IInventoryService>(inventory);
        services.AddSingleton<NavigationManager>(new StubNavigationManager());

        await using var provider = services.BuildServiceProvider();
        var loggerFactory = provider.GetRequiredService<ILoggerFactory>();
        var renderer = new HtmlRenderer(provider, loggerFactory);

        try
        {
            var html = await renderer.Dispatcher.InvokeAsync(async () =>
            {
                var root = await renderer.RenderComponentAsync<Products>(ParameterView.Empty);
                await root.QuiescenceTask;
                return root.ToHtmlString();
            });

            Assert.Contains("Select a tenant", html, StringComparison.Ordinal);

            // No data and no exception: the service was never asked for rows.
            Assert.Equal(0, inventory.ListProductsCalls);
            Assert.DoesNotContain("Loading products", html, StringComparison.Ordinal);
        }
        finally
        {
            await renderer.DisposeAsync();
        }
    }

    [Fact]
    public async Task ProductsPage_WithAResolvedTenant_RendersThatTenantsRows()
    {
        var tenantId = Guid.Parse("33333333-3333-4333-8333-333333333333");
        var tenant = new TenantState();
        tenant.Set(tenantId, "acme");

        var inventory = new RecordingInventoryService
        {
            Products =
            [
                new Product
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantId,
                    Sku = "SKU-RENDERED",
                    Name = "Rendered widget",
                    UnitPrice = 4.5m,
                    ReorderLevel = 0,
                    IsActive = true
                }
            ]
        };

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TenantState>(tenant);
        services.AddSingleton<IInventoryService>(inventory);
        services.AddSingleton<NavigationManager>(new StubNavigationManager());

        await using var provider = services.BuildServiceProvider();
        var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());

        try
        {
            var html = await renderer.Dispatcher.InvokeAsync(async () =>
            {
                var root = await renderer.RenderComponentAsync<Products>(ParameterView.Empty);
                await root.QuiescenceTask;
                return root.ToHtmlString();
            });

            Assert.Contains("SKU-RENDERED", html, StringComparison.Ordinal);
            Assert.Contains("Rendered widget", html, StringComparison.Ordinal);
            Assert.DoesNotContain("Select a tenant", html, StringComparison.Ordinal);
            Assert.Equal(1, inventory.ListProductsCalls);
        }
        finally
        {
            await renderer.DisposeAsync();
        }
    }

    private sealed class RecordingInventoryService : IInventoryService
    {
        public int ListProductsCalls { get; private set; }

        public IReadOnlyList<Product> Products { get; init; } = [];

        public Task<IReadOnlyList<Product>> ListProductsAsync(CancellationToken ct = default)
        {
            ListProductsCalls++;
            return Task.FromResult(Products);
        }

        public Task<Product?> GetProductAsync(Guid id, CancellationToken ct = default) =>
            Task.FromResult(Products.FirstOrDefault(product => product.Id == id));

        public Task<Product> CreateProductAsync(ProductInput input, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<Product> UpdateProductAsync(Guid id, ProductInput input, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task DeleteProductAsync(Guid id, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<StockLevel> AdjustStockAsync(
            Guid productId, int delta, string reason, string? note, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<StockMovement>> ListMovementsAsync(
            Guid productId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<StockMovement>>([]);
    }

    private sealed class StubNavigationManager : NavigationManager
    {
        public StubNavigationManager() =>
            Initialize("http://localhost/", "http://localhost/inventory/products?tenant=acme");

        protected override void NavigateToCore(string uri, bool forceLoad)
        {
        }
    }
}
