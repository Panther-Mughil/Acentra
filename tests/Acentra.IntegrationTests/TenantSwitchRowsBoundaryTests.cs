using Acentra.Domain.Abstractions;
using Acentra.Domain.Entities;
using Acentra.Infrastructure.ControlPlane;
using Acentra.Infrastructure.Inventory;
using Acentra.Infrastructure.TenantData;
using Acentra.Web.Auth;
using Acentra.Web.Components.Pages.Inventory;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Acentra.IntegrationTests;

/// <summary>
/// Architecture §5.6, driven against the <em>real</em> <see cref="TenantScopedRows{T}"/> and the
/// real seeded <c>acme</c>/<c>globex</c> tenant databases:
///
/// <para>
/// A Blazor Server circuit does not re-run <c>OnInitializedAsync</c> when the tenant changes, so a
/// page that loaded its rows once would keep rendering the previous tenant's data. This is the leak
/// vector the UI tenant switcher must close: the previous tenant's rows must be gone <em>before</em>
/// the next render, not just after the next await.
/// </para>
///
/// <para>
/// The rows are loaded through the production <see cref="InventoryService"/> (the same call the
/// products page makes) so the row counts and <c>TenantId</c> stamps are real DB values, not
/// fixtures. The two products this class creates carry GUID-unique SKUs and are deleted in teardown,
/// so the dev tenants are left exactly as they were.
/// </para>
/// </summary>
public sealed class TenantSwitchRowsBoundaryTests : IClassFixture<TenantSwitchRowsBoundaryTests.Fixture>
{
    private readonly Fixture _fixture;

    public TenantSwitchRowsBoundaryTests(Fixture fixture) => _fixture = fixture;

    private TenantDescriptor Acme => _fixture.Acme;

    private TenantDescriptor Globex => _fixture.Globex;

    // ------------------------------------------------------- item 1: rows really load for A

    [Fact]
    public async Task Switch_1_TenantARowsLoad_AndCarryTenantAId()
    {
        var state = new TenantState();
        state.Set(Acme.Id, Acme.Slug);

        using var rows = Holder(state);

        await rows.ReloadAsync();

        Assert.True(rows.TenantResolved);

        // The positive control: a real row for A is present, so "empty for B" below cannot be the
        // result of the loader never returning anything.
        var acmeRow = Assert.Single(rows.Rows, product => product.Sku == _fixture.AcmeSku);
        Assert.Equal(Acme.Id, acmeRow.TenantId);
        Assert.All(rows.Rows, product => Assert.Equal(Acme.Id, product.TenantId));
    }

    // ------------------------------------------------------------ item 2: the switch raises Changed

    [Fact]
    public async Task Switch_2_MovingTheCircuitToTenantB_RaisesChanged_ExactlyOnce()
    {
        var state = new TenantState();
        state.Set(Acme.Id, Acme.Slug);

        using var rows = Holder(state);

        var changed = 0;
        state.Changed += () => changed++;

        // This is the single mutation the switcher performs; the rows holder must observe it.
        state.Set(Globex.Id, Globex.Slug);

        Assert.Equal(1, changed);

        await WaitForAsync(() => !rows.IsLoading);
    }

    // ------------------------------------- item 3: the clear is synchronous, before the re-query

    [Fact]
    public async Task Switch_3_TenantARowsAreClearedSynchronously_BeforeTheTenantBRequeryIsAwaited()
    {
        var state = new TenantState();
        state.Set(Acme.Id, Acme.Slug);

        var gateForB = new TaskCompletionSource();

        using var rows = new TenantScopedRows<Product>(
            state,
            product => product.TenantId,
            async (tenantId, ct) =>
            {
                if (tenantId == Globex.Id)
                {
                    await gateForB.Task; // the re-query is deliberately left pending
                }

                return await _fixture.ServiceFor(tenantId).ListProductsAsync(ct);
            });

        await rows.ReloadAsync();
        Assert.Contains(rows.Rows, product => product.Sku == _fixture.AcmeSku);

        // The switch. No await occurs between this line and the assertion below.
        state.Set(Globex.Id, Globex.Slug);

        // This is the whole requirement: not one render can see the previous tenant's rows.
        Assert.Empty(rows.Rows);
        Assert.True(rows.IsLoading); // the B re-query is still in flight, proving the clear was not it

        gateForB.SetResult();
        await WaitForAsync(() => rows.Rows.Count > 0 && rows.Rows.All(product => product.TenantId == Globex.Id));
    }

    // ------------------------------------------ item 4: only B's rows remain after the re-query

    [Fact]
    public async Task Switch_4_AfterTheSwitch_OnlyTenantBRowsAreReturned_AndTenantARowsAreGone()
    {
        var state = new TenantState();
        state.Set(Acme.Id, Acme.Slug);

        using var rows = Holder(state);

        await rows.ReloadAsync();
        Assert.Contains(rows.Rows, product => product.Sku == _fixture.AcmeSku);

        state.Set(Globex.Id, Globex.Slug);

        await WaitForAsync(() => rows.Rows.Count > 0 && rows.Rows.All(product => product.TenantId == Globex.Id));

        Assert.Contains(rows.Rows, product => product.Sku == _fixture.GlobexSku);
        Assert.All(rows.Rows, product => Assert.Equal(Globex.Id, product.TenantId));
        Assert.DoesNotContain(rows.Rows, product => product.Sku == _fixture.AcmeSku);
    }

    // ---------------------------------------------------- item 5: the in-flight load is discarded

    [Fact]
    public async Task Switch_5_ALoadForTenantAStillInFlightWhenTheTenantSwitchesToB_IsDiscarded()
    {
        var state = new TenantState();
        state.Set(Acme.Id, Acme.Slug);

        var gateForA = new TaskCompletionSource<IReadOnlyList<Product>>();

        using var rows = new TenantScopedRows<Product>(
            state,
            product => product.TenantId,
            async (tenantId, ct) =>
            {
                if (tenantId == Acme.Id)
                {
                    return await gateForA.Task; // A's result is held until after the switch
                }

                return await _fixture.ServiceFor(tenantId).ListProductsAsync(ct);
            });

        // A's load starts and is still in flight …
        var inFlight = rows.ReloadAsync();

        // … when the tenant switches to B.
        state.Set(Globex.Id, Globex.Slug);

        await WaitForAsync(() => rows.Rows.Count > 0 && rows.Rows.All(product => product.TenantId == Globex.Id));

        // A's result finally arrives — it must never be assigned.
        gateForA.SetResult(await _fixture.ServiceFor(Acme.Id).ListProductsAsync());
        await inFlight;

        Assert.All(rows.Rows, product => Assert.Equal(Globex.Id, product.TenantId));
        Assert.DoesNotContain(rows.Rows, product => product.Sku == _fixture.AcmeSku);
    }

    // -------------------------------------------- item 6: the defensive TenantId re-check

    [Fact]
    public async Task Switch_6_ARowCarryingAForeignTenantId_IsDroppedByTheRowsGetter()
    {
        var state = new TenantState();
        state.Set(Acme.Id, Acme.Slug);

        using var rows = Holder(state);

        await rows.ReloadAsync();

        // Positive: the row is present and carries A's id.
        var acmeRow = Assert.Single(rows.Rows, product => product.Sku == _fixture.AcmeSku);
        Assert.Equal(Acme.Id, acmeRow.TenantId);

        // Simulate a foreign row that somehow reached the collection (e.g. a missed Changed event):
        // mutate the detached row's TenantId to the other tenant's.
        acmeRow.TenantId = Globex.Id;

        // The Rows getter must drop it rather than render it.
        Assert.Empty(rows.Rows);
    }

    // --------------------------------------------------------------------------------- helpers

    private TenantScopedRows<Product> Holder(TenantState state) =>
        new(state, product => product.TenantId, (tenantId, ct) => _fixture.ServiceFor(tenantId).ListProductsAsync(ct));

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 400 && !condition(); attempt++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition(), "The expected state was not reached in time.");
    }

    /// <summary>
    /// Resolves the real seeded <c>acme</c>/<c>globex</c> tenants from the control plane, seeds one
    /// product in each with a GUID-unique SKU, and deletes them in teardown. Real databases, real
    /// <see cref="InventoryService"/>, and no leftover rows.
    /// </summary>
    public sealed class Fixture : IAsyncLifetime
    {
        private readonly ControlPlaneDbContext _controlPlane;
        private readonly TenantConnectionStrings _connections;
        private readonly List<(InventoryService Service, Guid ProductId)> _created = [];

        public Fixture()
        {
            var options = new DbContextOptionsBuilder<ControlPlaneDbContext>()
                .UseNpgsql(IsolationTestSupport.Configuration.GetConnectionString("ControlPlane"))
                .Options;

            _controlPlane = new ControlPlaneDbContext(options);
            _connections = TenantConnectionStrings.FromConfiguration(IsolationTestSupport.Configuration);
        }

        public TenantDescriptor Acme { get; private set; } = default!;

        public TenantDescriptor Globex { get; private set; } = default!;

        public string AcmeSku { get; private set; } = string.Empty;

        public string GlobexSku { get; private set; } = string.Empty;

        private InventoryService AcmeService { get; set; } = default!;

        private InventoryService GlobexService { get; set; } = default!;

        public async Task InitializeAsync()
        {
            await IsolationTestSupport.AssertPostgresReachableAsync();

            var registry = new TenantRegistry(_controlPlane, NullLogger<TenantRegistry>.Instance);

            Acme = await registry.FindBySlugAsync(TenantSeeder.AcmeSlug, CancellationToken.None)
                ?? throw new InvalidOperationException("The dev tenant 'acme' is not seeded.");
            Globex = await registry.FindBySlugAsync(TenantSeeder.GlobexSlug, CancellationToken.None)
                ?? throw new InvalidOperationException("The dev tenant 'globex' is not seeded.");

            AcmeService = ServiceForDescriptor(Acme);
            GlobexService = ServiceForDescriptor(Globex);

            var marker = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
            AcmeSku = $"SKU-SWITCH-A-{marker}";
            GlobexSku = $"SKU-SWITCH-B-{marker}";

            var acmeProduct = await AcmeService.CreateProductAsync(
                new ProductInput(AcmeSku, "Tenant-switch boundary A", null, 1m, 0, true));
            _created.Add((AcmeService, acmeProduct.Id));

            var globexProduct = await GlobexService.CreateProductAsync(
                new ProductInput(GlobexSku, "Tenant-switch boundary B", null, 2m, 0, true));
            _created.Add((GlobexService, globexProduct.Id));
        }

        public async Task DisposeAsync()
        {
            foreach (var (service, productId) in _created)
            {
                try
                {
                    await service.DeleteProductAsync(productId);
                }
                catch (Exception)
                {
                    // Best-effort cleanup: a failed delete must not mask the test's own result.
                }
            }

            await _controlPlane.DisposeAsync();
        }

        public InventoryService ServiceFor(Guid tenantId) =>
            tenantId == Acme.Id ? AcmeService : GlobexService;

        private InventoryService ServiceForDescriptor(TenantDescriptor tenant) =>
            new(new TenantDbContextFactory(
                new ResolvedTenantContext(tenant.Id, tenant.Slug),
                _connections));
    }
}
