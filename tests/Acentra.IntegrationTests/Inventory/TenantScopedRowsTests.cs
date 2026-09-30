using Acentra.Domain.Entities;
using Acentra.Web.Auth;
using Acentra.Web.Components.Pages.Inventory;

namespace Acentra.IntegrationTests;

/// <summary>
/// REQ-005 requirement #9's leak vector, driven at the state level: the collection an inventory page
/// binds to must contain the previous tenant's rows for exactly zero renders after the tenant changes.
///
/// A Blazor Server circuit does <em>not</em> re-run <c>OnInitializedAsync</c> when the tenant changes
/// (architecture §4.1), so <see cref="TenantScopedRows{T}"/> is the seam that guarantees it — and
/// this is the test REQ-009 depends on (it clears <c>TenantState</c> to remove access). The sequence
/// asserted here is exactly what the switcher and a REQ-009 expiry trigger.
/// </summary>
public sealed class TenantScopedRowsTests
{
    private static readonly Guid TenantAId = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid TenantBId = Guid.Parse("22222222-2222-4222-8222-222222222222");

    [Fact]
    public async Task SwitchingTenant_BlanksThePreviousRowsImmediately_ThenLoadsTheNewTenants()
    {
        var state = new TenantState();
        state.Set(TenantAId, "a");

        var loader = new RecordingLoader();
        using var rows = new TenantScopedRows<Product>(state, product => product.TenantId, loader.Load);

        await rows.ReloadAsync();

        var acmeRow = Assert.Single(rows.Rows);
        Assert.Equal(TenantAId, acmeRow.TenantId);

        // The switch. B's load is deliberately left pending so the synchronous clear is observable.
        loader.HoldTenant(TenantBId);
        state.Set(TenantBId, "b");

        // This is the whole requirement: not one render can see the previous tenant's row.
        Assert.Empty(rows.Rows);
        Assert.True(rows.TenantResolved);
        Assert.Null(rows.Error);

        // …and the page re-queries on its own, without OnInitializedAsync running again.
        loader.ReleaseTenant(TenantBId);
        await WaitForAsync(() => rows.Rows.Count == 1);

        var globexRow = Assert.Single(rows.Rows);
        Assert.Equal(TenantBId, globexRow.TenantId);
        Assert.Equal(TenantBId, loader.Calls[^1]);
    }

    [Fact]
    public async Task ALoadThatLandsAfterTheTenantChanged_IsDiscarded()
    {
        var state = new TenantState();
        state.Set(TenantAId, "a");

        var loader = new RecordingLoader();
        using var rows = new TenantScopedRows<Product>(state, product => product.TenantId, loader.Load);

        // A's load is still in flight when the tenant changes.
        loader.HoldTenant(TenantAId);
        var inFlight = rows.ReloadAsync();

        state.Set(TenantBId, "b");
        await WaitForAsync(() => rows.Rows.Count == 1 && rows.Rows[0].TenantId == TenantBId);

        // The stale result finally arrives: it must not be adopted.
        loader.ReleaseTenant(TenantAId);
        await inFlight;

        var row = Assert.Single(rows.Rows);
        Assert.Equal(TenantBId, row.TenantId);
        Assert.DoesNotContain(rows.Rows, candidate => candidate.TenantId == TenantAId);
    }

    [Fact]
    public async Task RowsCarryingAnotherTenantsId_AreNeverReturned()
    {
        var state = new TenantState();
        state.Set(TenantAId, "a");

        // A buggy loader that returns another tenant's row: the defensive re-check drops it.
        using var rows = new TenantScopedRows<Product>(
            state,
            product => product.TenantId,
            (_, _) => Task.FromResult<IReadOnlyList<Product>>([Product(TenantBId, "SKU-OTHER")]));

        await rows.ReloadAsync();

        Assert.Empty(rows.Rows);
    }

    [Fact]
    public async Task ClearingTheTenant_DropsTheRows_AndStopsServingData()
    {
        var state = new TenantState();
        state.Set(TenantAId, "a");

        var loader = new RecordingLoader();
        using var rows = new TenantScopedRows<Product>(state, product => product.TenantId, loader.Load);

        await rows.ReloadAsync();
        Assert.Single(rows.Rows);

        var callsBeforeClear = loader.Calls.Count;

        // The REQ-009 expiry path: access is removed, so the rows must go.
        state.Clear();

        Assert.Empty(rows.Rows);
        Assert.False(rows.TenantResolved);

        // Nothing is re-queried: there is no tenant to query for.
        await Task.Delay(50);
        Assert.Equal(callsBeforeClear, loader.Calls.Count);
        Assert.Empty(rows.Rows);
    }

    [Fact]
    public async Task AnUnresolvedTenant_ShowsNoData_AndRaisesNoError()
    {
        var state = new TenantState();
        var loader = new RecordingLoader();

        using var rows = new TenantScopedRows<Product>(state, product => product.TenantId, loader.Load);

        Assert.False(rows.TenantResolved);

        await rows.ReloadAsync();

        // "Select a tenant" is a state, not a failure: no data, no exception, no error message.
        Assert.Empty(rows.Rows);
        Assert.Null(rows.Error);
        Assert.False(rows.TenantResolved);
        Assert.Empty(loader.Calls);
    }

    // ----------------------------------------------------------------------- helpers

    /// <summary>A loader whose per-tenant result can be held open, so races are deterministic.</summary>
    private sealed class RecordingLoader
    {
        private readonly Dictionary<Guid, TaskCompletionSource<IReadOnlyList<Product>>> _gates = [];
        private readonly List<Guid> _calls = [];

        public IReadOnlyList<Guid> Calls => _calls;

        public void HoldTenant(Guid tenantId) => _gates[tenantId] = new TaskCompletionSource<IReadOnlyList<Product>>();

        public void ReleaseTenant(Guid tenantId)
        {
            if (_gates.Remove(tenantId, out var gate))
            {
                gate.TrySetResult([Product(tenantId, $"SKU-{tenantId:N}"[..12])]);
            }
        }

        public Task<IReadOnlyList<Product>> Load(Guid tenantId, CancellationToken ct)
        {
            _calls.Add(tenantId);

            if (_gates.TryGetValue(tenantId, out var gate))
            {
                return gate.Task;
            }

            return Task.FromResult<IReadOnlyList<Product>>([Product(tenantId, $"SKU-{tenantId:N}"[..12])]);
        }
    }

    private static Product Product(Guid tenantId, string sku) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = tenantId,
        Sku = sku,
        Name = $"Product for {tenantId}",
        UnitPrice = 1m,
        ReorderLevel = 0,
        IsActive = true,
        CreatedUtc = DateTime.UtcNow,
        UpdatedUtc = DateTime.UtcNow
    };

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 200 && !condition(); attempt++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition(), "The expected state was not reached in time.");
    }
}
