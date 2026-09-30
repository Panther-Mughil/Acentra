using System.Globalization;
using System.Security.Claims;
using Acentra.Domain.Abstractions;
using Acentra.Infrastructure.ControlPlane;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Acentra.Web.Auth;

/// <summary>
/// Bridges the HTTP request scope into the Blazor circuit scope (§4.1 of the architecture doc),
/// and keeps the circuit's tenant authorization from going stale.
///
/// With interactive Server the middleware runs once per circuit (at the <c>/_blazor</c>
/// handshake), while <see cref="TenantState"/> is resolved in the circuit's own DI scope.
/// This handler copies the tenant the middleware resolved on that request - published in
/// <c>HttpContext.Items</c> - into this circuit's <see cref="TenantState"/>, then re-checks that
/// authorization on a bounded interval (REQ-009).
///
/// Fail closed: if there is nothing to seed (no HttpContext, or no resolved tenant, e.g. the
/// circuit was opened from a tenant-agnostic page), the circuit stays unresolved and tenant
/// pages must render a "select a tenant" state. A default tenant is never chosen.
///
/// A circuit is long-lived and makes no per-interaction HTTP request, so without the revalidation
/// loop a revoked membership or suspended tenant would keep working for as long as the tab stays
/// open. <see cref="RevalidateAsync"/> re-reads live membership/status - and clears, never
/// switches - when it is no longer valid.
/// </summary>
public sealed class TenantCircuitHandler : CircuitHandler
{
    private readonly TenantState _state;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ITenantRegistry _registry;
    private readonly ILogger<TenantCircuitHandler> _logger;
    private readonly TimeSpan _revalidationInterval;

    private readonly object _sync = new();

    // Captured once, at circuit open. Revalidation never touches HttpContext again - there is no
    // request to read it from after the handshake.
    private Guid _tenantId = Guid.Empty;
    private string _slug = string.Empty;
    private Guid? _userId;

    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;

    // 0 == idle, 1 == a check is in flight. Serialises the loop so checks never stack.
    private int _revalidating;

    /// <summary>
    /// Test / bridge seam. Production always resolves the five-argument constructor through DI,
    /// which supplies the live <see cref="ITenantRegistry"/>. This overload exists only for the
    /// circuit-bridge tests that seed <see cref="TenantState"/> directly; it fails closed (see
    /// <see cref="NullTenantRegistry"/>) and applies the default interval.
    /// </summary>
    public TenantCircuitHandler(
        TenantState state,
        IHttpContextAccessor httpContextAccessor)
        : this(
            state,
            httpContextAccessor,
            NullTenantRegistry.Instance,
            configuration: null,
            NullLogger<TenantCircuitHandler>.Instance)
    {
    }

    public TenantCircuitHandler(
        TenantState state,
        IHttpContextAccessor httpContextAccessor,
        ITenantRegistry registry,
        IConfiguration? configuration,
        ILogger<TenantCircuitHandler> logger)
    {
        _state = state;
        _httpContextAccessor = httpContextAccessor;
        _registry = registry;
        _logger = logger;
        _revalidationInterval = ResolveRevalidationInterval(configuration);
    }

    /// <summary>The effective (already-clamped) revalidation interval for this circuit.</summary>
    public TimeSpan RevalidationInterval => _revalidationInterval;

    /// <summary>
    /// The circuit's background revalidation loop, if one was started. Exposed so lifecycle
    /// tests can assert it is cancelled and does not outlive the circuit.
    /// </summary>
    public Task? RevalidationLoopTask
    {
        get
        {
            lock (_sync)
            {
                return _loopTask;
            }
        }
    }

    public override Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        var httpContext = _httpContextAccessor.HttpContext;
        var items = httpContext?.Items;

        if (items is not null &&
            items.TryGetValue(TenantResolutionConstants.HttpContextItemKey, out var value) &&
            value is TenantDescriptor descriptor &&
            descriptor.Id != Guid.Empty)
        {
            _state.Set(descriptor.Id, descriptor.Slug);

            _tenantId = descriptor.Id;
            _slug = descriptor.Slug;

            // The user id is read from the same handshake HttpContext that already carries the
            // tenant, and captured here - never again. IHttpContextAccessor is unusable, and
            // wrong, once the circuit is running.
            _userId = GetUserId(httpContext!.User);

            StartRevalidationLoop();
        }

        return Task.CompletedTask;
    }

    public override Task OnConnectionUpAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        // Reconnecting to an existing circuit: resume the loop (idempotent - one loop per circuit).
        if (_tenantId != Guid.Empty)
        {
            StartRevalidationLoop();
        }

        return Task.CompletedTask;
    }

    public override Task OnConnectionDownAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        // The circuit survives a dropped connection, but nothing is being served while it is down.
        // Stop the loop; OnConnectionUpAsync restarts it, and OnCircuitClosedAsync still bounds the
        // lifetime in case the client never returns.
        return StopRevalidationLoopAsync();
    }

    public override Task OnCircuitClosedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        // Await the loop so no timer or task can outlive the circuit.
        return StopRevalidationLoopAsync();
    }

    /// <summary>
    /// Re-checks this circuit's tenant against live membership and tenant status.
    /// Returns <c>false</c> when access has been removed (and <see cref="TenantState"/> has been
    /// cleared); returns <c>true</c> when access is still valid, in which case the state is left
    /// completely untouched - no reassignment and no <see cref="TenantState.Changed"/>.
    ///
    /// Fail-closed decision table:
    /// <list type="bullet">
    ///   <item>no usable user id -> clear;</item>
    ///   <item>tenant no longer resolvable (unknown / suspended / deleted) -> clear;</item>
    ///   <item>caller is no longer a member of this circuit's tenant -> clear;</item>
    ///   <item>otherwise -> untouched.</item>
    /// </list>
    /// A registry error is treated as "no longer authorized". The circuit is never switched to a
    /// different tenant the user still belongs to - it is cleared only.
    ///
    /// This is the testable seam: it is a plain awaitable method that takes no timer and no
    /// real circuit, so tests can drive it directly with a fake <see cref="ITenantRegistry"/>.
    /// </summary>
    public async Task<bool> RevalidateAsync(CancellationToken ct)
    {
        var tenantId = _tenantId;

        // Nothing was seeded (tenant-agnostic circuit): there is no authorization to expire, and
        // clearing would wrongly behave as if access had been revoked.
        if (tenantId == Guid.Empty)
        {
            return true;
        }

        if (_userId is not { } userId)
        {
            return Expire("the circuit principal has no usable user id");
        }

        IReadOnlyList<TenantDescriptor> memberships;

        try
        {
            memberships = await _registry.FindForUserAsync(userId, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // circuit closing mid-check: not a revocation
        }
        catch (Exception ex)
        {
            // Fail closed: an unreachable/erroring registry means "no longer authorized".
            return Expire($"the tenant registry could not be reached ({ex.GetType().Name})");
        }

        if (memberships.Any(m => m.Id == tenantId))
        {
            // Still a member of *this* tenant: leave TenantState exactly as it is. Reassigning, or
            // firing Changed, would needlessly blow away the UI every interval.
            return true;
        }

        // Never switch: the user may still belong to other tenants, but this circuit's tenant is
        // no longer authorized, so it is cleared - not retargeted.
        ct.ThrowIfCancellationRequested();

        var reason = await DescribeLossAsync(ct).ConfigureAwait(false);

        return Expire(reason);
    }

    /// <summary>
    /// Binds <c>Tenant:RevalidationInterval</c>, defaulting to five minutes and clamped up to a
    /// positive floor so a zero/negative value can never silently disable the guarantee.
    /// </summary>
    public static TimeSpan ResolveRevalidationInterval(IConfiguration? configuration)
    {
        var raw = configuration?[TenantResolutionConstants.RevalidationIntervalKey];

        var configured =
            TimeSpan.TryParse(raw, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : TenantResolutionConstants.DefaultRevalidationInterval;

        return configured < TenantResolutionConstants.MinimumRevalidationInterval
            ? TenantResolutionConstants.MinimumRevalidationInterval
            : configured;
    }

    /// <summary>
    /// Distinguishes the two clearing causes for the log only; the decision (clear) is identical.
    /// </summary>
    private async Task<string> DescribeLossAsync(CancellationToken ct)
    {
        try
        {
            var tenant = await _registry.FindBySlugAsync(_slug, ct).ConfigureAwait(false);

            return tenant is null
                ? "the tenant is no longer resolvable (unknown or suspended)"
                : "the user is no longer a member";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return $"the tenant registry could not be reached ({ex.GetType().Name})";
        }
    }

    /// <summary>
    /// The single expiry path. Clears <see cref="TenantState"/> exactly once (so
    /// <see cref="TenantState.Changed"/> fires once and tenant pages drop their cached rows) and
    /// logs the precise cause - which is never surfaced to the client.
    /// </summary>
    private bool Expire(string reason)
    {
        _logger.LogWarning(
            "Circuit tenant authorization for '{Slug}' expired: {Reason}. Clearing tenant state; " +
            "the circuit is never switched to another tenant.",
            _slug,
            reason);

        // Stop treating this circuit as tenant-bearing so subsequent ticks do not clear (and
        // re-raise Changed) again.
        _tenantId = Guid.Empty;

        _state.Clear();

        return false;
    }

    private void StartRevalidationLoop()
    {
        lock (_sync)
        {
            if (_loopCts is not null)
            {
                return; // one loop per circuit
            }

            var cts = new CancellationTokenSource();
            _loopCts = cts;
            _loopTask = RunRevalidationLoopAsync(cts);
        }
    }

    private async Task RunRevalidationLoopAsync(CancellationTokenSource cts)
    {
        var token = cts.Token;
        using var timer = new PeriodicTimer(_revalidationInterval);

        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                // Overlap protection: if the previous check is still running, skip this tick
                // rather than stacking checks.
                if (Interlocked.CompareExchange(ref _revalidating, 1, 0) != 0)
                {
                    continue;
                }

                try
                {
                    await RevalidateAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break; // circuit closing / connection down
                }
                catch (Exception ex)
                {
                    // RevalidateAsync already fails closed; this only guards a throwing subscriber.
                    _logger.LogWarning(
                        ex, "Revalidation tick for tenant '{Slug}' failed unexpectedly.", _slug);
                }
                finally
                {
                    Interlocked.Exchange(ref _revalidating, 0);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on circuit close / connection down; never logged as an error.
        }
    }

    private async Task StopRevalidationLoopAsync()
    {
        CancellationTokenSource? cts;
        Task? task;

        lock (_sync)
        {
            cts = _loopCts;
            task = _loopTask;
            _loopCts = null;
        }

        if (cts is null)
        {
            return;
        }

        try
        {
            await cts.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // Already stopped.
        }

        if (task is not null)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected: cancellation is how the loop ends.
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex, "Revalidation loop for tenant '{Slug}' ended with an error.", _slug);
            }
        }

        cts.Dispose();
    }

    private static Guid? GetUserId(ClaimsPrincipal? user)
    {
        var value = user?.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        return Guid.TryParse(value, out var id) ? id : null;
    }

    /// <summary>
    /// Fail-closed registry used only by the two-argument bridge/test constructor. It reports no
    /// memberships, so a revalidation tick clears the tenant rather than silently keeping stale
    /// authorization. Production always supplies the real registry through DI.
    /// </summary>
    private sealed class NullTenantRegistry : ITenantRegistry
    {
        public static readonly NullTenantRegistry Instance = new();

        public Task<TenantDescriptor?> FindBySlugAsync(string slug, CancellationToken ct) =>
            Task.FromResult<TenantDescriptor?>(null);

        public Task<IReadOnlyList<TenantDescriptor>> FindForUserAsync(Guid userId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<TenantDescriptor>>([]);
    }
}
