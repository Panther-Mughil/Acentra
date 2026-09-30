using Acentra.Web.Auth;

namespace Acentra.Web.Components.Pages.Inventory;

/// <summary>
/// Holds one page's tenant-scoped rows so that a tenant change cannot leave the previous tenant's
/// data on screen.
///
/// <para>
/// This is the REQ-005 §4/§5 invalidation seam (architecture §4.1, §5.6 — "Blazor circuits outliving
/// a request"): a Blazor Server circuit does <em>not</em> re-run <c>OnInitializedAsync</c> when the
/// tenant changes, so a page that simply loaded rows once would keep rendering them. This holder
/// subscribes to <see cref="TenantState.Changed"/> and, on fire:
/// </para>
/// <list type="number">
///   <item>clears the rows <em>synchronously</em> — before any re-query is awaited — so there is no
///         window in which the old tenant's rows are still renderable;</item>
///   <item>bumps a load version so an in-flight load for the previous tenant is discarded when it
///         returns (and never assigned);</item>
///   <item>re-queries for the current tenant.</item>
/// </list>
///
/// <para>
/// It is deliberately a plain class, not a component: the pages stay thin (and replaceable) and this
/// behaviour is testable without a renderer. It holds no <c>DbContext</c> — the loader callback is
/// the page's call into <c>IInventoryService</c>, which opens its own short-lived context per call.
/// </para>
///
/// <para>
/// Defensive re-check on top of the subscription: <see cref="Rows"/> never returns a row whose
/// <c>TenantId</c> is not the current tenant's, even if an event were somehow missed.
/// </para>
/// </summary>
/// <typeparam name="T">A tenant-owned row (every one carries a non-null <c>TenantId</c>).</typeparam>
public sealed class TenantScopedRows<T> : IDisposable
{
    private readonly TenantState _tenant;
    private readonly Func<T, Guid> _tenantIdOf;
    private readonly Func<Guid, CancellationToken, Task<IReadOnlyList<T>>> _load;

    private IReadOnlyList<T> _rows = [];
    private Guid _loadedTenantId = Guid.Empty;
    private int _loadVersion;
    private bool _disposed;

    public TenantScopedRows(
        TenantState tenant,
        Func<T, Guid> tenantIdOf,
        Func<Guid, CancellationToken, Task<IReadOnlyList<T>>> load)
    {
        _tenant = tenant ?? throw new ArgumentNullException(nameof(tenant));
        _tenantIdOf = tenantIdOf ?? throw new ArgumentNullException(nameof(tenantIdOf));
        _load = load ?? throw new ArgumentNullException(nameof(load));

        _tenant.Changed += OnTenantChanged;
    }

    /// <summary>
    /// The rows loaded for the <em>current</em> tenant. Returns an empty list while nothing is
    /// loaded, while a load is in flight and whenever the tenant changes.
    /// </summary>
    public IReadOnlyList<T> Rows
    {
        get
        {
            if (_rows.Count == 0)
            {
                return _rows;
            }

            if (_loadedTenantId != _tenant.TenantId ||
                _rows.Any(row => _tenantIdOf(row) != _tenant.TenantId))
            {
                // A row that does not belong to the tenant we are rendering for is dropped, never
                // shown. This can only trigger if the Changed event was missed.
                Clear();
            }

            return _rows;
        }
    }

    /// <summary>True when a tenant is resolved; false means the page renders its "select a tenant" state.</summary>
    public bool TenantResolved => _tenant.IsResolved;

    /// <summary>Set when the last load failed (for example no tenant resolved). Rows are still empty.</summary>
    public string? Error { get; private set; }

    /// <summary>True while a load is in flight.</summary>
    public bool IsLoading { get; private set; }

    /// <summary>Raised whenever the rows, <see cref="IsLoading"/> or <see cref="Error"/> may have changed.</summary>
    public event Action? Updated;

    /// <summary>
    /// Loads the rows for the current tenant. A result that arrives after the tenant changed (or
    /// after a newer load started) is discarded and never assigned.
    /// </summary>
    public async Task ReloadAsync(CancellationToken ct = default)
    {
        var version = ++_loadVersion;
        var tenantId = _tenant.TenantId;

        Error = null;

        if (tenantId == Guid.Empty)
        {
            Clear();
            Updated?.Invoke();
            return;
        }

        IsLoading = true;
        Updated?.Invoke();

        try
        {
            var rows = await _load(tenantId, ct);

            if (version != _loadVersion || tenantId != _tenant.TenantId)
            {
                return; // Stale: the tenant changed while this load was in flight.
            }

            // Belt and braces: only rows actually carrying the tenant we asked for are kept.
            _rows = [.. rows.Where(row => _tenantIdOf(row) == tenantId)];
            _loadedTenantId = tenantId;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (version == _loadVersion)
            {
                Clear();
                Error = exception.Message;
            }
        }
        finally
        {
            if (version == _loadVersion)
            {
                IsLoading = false;
                Updated?.Invoke();
            }
        }
    }

    private void OnTenantChanged()
    {
        // 1. Kill the in-flight load.
        _loadVersion++;

        // 2. Drop the previous tenant's rows synchronously — this is the leak vector, and it closes
        //    here rather than after an await.
        Clear();

        Updated?.Invoke();

        // 3. Re-query for whatever the tenant is now. The page re-renders from Updated.
        _ = ReloadAsync();
    }

    private void Clear()
    {
        _rows = [];
        _loadedTenantId = Guid.Empty;
        Error = null;
        IsLoading = false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _tenant.Changed -= OnTenantChanged;
    }
}
