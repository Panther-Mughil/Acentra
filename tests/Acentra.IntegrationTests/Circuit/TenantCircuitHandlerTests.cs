using System.Security.Claims;
using Acentra.Domain.Abstractions;
using Acentra.Infrastructure.ControlPlane;
using Acentra.Web.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Acentra.IntegrationTests;

/// <summary>
/// REQ-009: a Blazor circuit makes no per-interaction HTTP request, so its tenant authorization
/// must expire on a bounded interval. These drive the testable seam
/// (<see cref="TenantCircuitHandler.RevalidateAsync"/>) directly with a fake registry — no timer,
/// no real circuit — for every branch of the fail-closed decision table, plus the interval clamp
/// and the timer/loop lifecycle.
/// </summary>
public sealed class TenantCircuitHandlerTests
{
    private static readonly Guid AcmeId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid GlobexId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid UserId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    // ----------------------------------------------------------------- decision table

    [Fact]
    public async Task Revalidate_WhenMembershipRevoked_ClearsStateAndReturnsFalse()
    {
        // The user is still a member of Globex, but was removed from Acme (this circuit).
        var registry = new FakeTenantRegistry
        {
            Memberships = [Descriptor(GlobexId, "globex")],
            TenantBySlug = Descriptor(AcmeId, "acme")
        };

        var (handler, state) = await OpenCircuitAsync(registry, AcmeId, "acme", UserId);

        var changed = 0;
        state.Changed += () => changed++;

        var result = await handler.RevalidateAsync(CancellationToken.None);

        Assert.False(result);
        Assert.False(state.IsResolved);
        Assert.Equal(Guid.Empty, state.TenantId);
        Assert.Equal(string.Empty, state.Slug);

        // Never switch: Globex is available to the user, but the circuit must not be retargeted.
        Assert.NotEqual(GlobexId, state.TenantId);

        // TenantState.Clear() fires Changed exactly once per expiry.
        Assert.Equal(1, changed);
    }

    [Fact]
    public async Task Revalidate_WhenTenantSuspendedOrUnknown_ClearsStateAndReturnsFalse()
    {
        // A suspended/unknown tenant never appears in the (active-only) membership list, and
        // FindBySlugAsync returns null for it.
        var registry = new FakeTenantRegistry
        {
            Memberships = [],
            TenantBySlug = null
        };

        var (handler, state) = await OpenCircuitAsync(registry, AcmeId, "acme", UserId);

        var result = await handler.RevalidateAsync(CancellationToken.None);

        Assert.False(result);
        Assert.False(state.IsResolved);
        Assert.Equal(Guid.Empty, state.TenantId);
    }

    [Fact]
    public async Task Revalidate_WhenPrincipalHasNoUsableUserId_ClearsStateAndReturnsFalse()
    {
        var registry = new FakeTenantRegistry
        {
            Memberships = [Descriptor(AcmeId, "acme")],
            TenantBySlug = Descriptor(AcmeId, "acme")
        };

        // Seeded tenant, but the principal carries no (parseable) NameIdentifier.
        var (handler, state) = await OpenCircuitAsync(registry, AcmeId, "acme", userId: null);

        var result = await handler.RevalidateAsync(CancellationToken.None);

        Assert.False(result);
        Assert.False(state.IsResolved);

        // No membership lookup should even have been attempted.
        Assert.Equal(0, registry.FindForUserCallCount);
    }

    [Fact]
    public async Task Revalidate_WhenStillValid_ReturnsTrueAndLeavesStateCompletelyUntouched()
    {
        var registry = new FakeTenantRegistry
        {
            Memberships = [Descriptor(GlobexId, "globex"), Descriptor(AcmeId, "acme")],
            TenantBySlug = Descriptor(AcmeId, "acme")
        };

        var (handler, state) = await OpenCircuitAsync(registry, AcmeId, "acme", UserId);

        var changed = 0;
        state.Changed += () => changed++;

        var result = await handler.RevalidateAsync(CancellationToken.None);

        Assert.True(result);
        Assert.True(state.IsResolved);
        Assert.Equal(AcmeId, state.TenantId);
        Assert.Equal("acme", state.Slug);

        // No spurious Changed events: re-raising would needlessly blow away the UI every interval.
        Assert.Equal(0, changed);

        // The valid path does not need the disambiguating tenant lookup.
        Assert.Equal(1, registry.FindForUserCallCount);
        Assert.Equal(0, registry.FindBySlugCallCount);
    }

    [Fact]
    public async Task Revalidate_WhenRegistryThrows_FailsClosed()
    {
        var registry = new FakeTenantRegistry
        {
            Failure = new InvalidOperationException("control-plane database unreachable")
        };

        var (handler, state) = await OpenCircuitAsync(registry, AcmeId, "acme", UserId);

        var result = await handler.RevalidateAsync(CancellationToken.None);

        Assert.False(result);
        Assert.False(state.IsResolved);
    }

    [Fact]
    public async Task Revalidate_WithNoSeededTenant_ReturnsTrueAndDoesNotClear()
    {
        var registry = new FakeTenantRegistry();

        var state = new TenantState();
        var handler = CreateHandler(state, registry, UserId, tenantId: null);

        var changed = 0;
        state.Changed += () => changed++;

        var result = await handler.RevalidateAsync(CancellationToken.None);

        Assert.True(result);
        Assert.False(state.IsResolved);
        Assert.Equal(0, changed);
        Assert.Equal(0, registry.FindForUserCallCount);
    }

    [Fact]
    public async Task Revalidate_AfterExpiry_DoesNotClearAgainOnLaterTicks()
    {
        var registry = new FakeTenantRegistry
        {
            Memberships = [],
            TenantBySlug = null
        };

        var (handler, state) = await OpenCircuitAsync(registry, AcmeId, "acme", UserId);

        var changed = 0;
        state.Changed += () => changed++;

        Assert.False(await handler.RevalidateAsync(CancellationToken.None));
        Assert.True(await handler.RevalidateAsync(CancellationToken.None));

        // Exactly once per expiry, not once per subsequent tick.
        Assert.Equal(1, changed);
    }

    // ---------------------------------------------------------------------- interval clamp

    [Fact]
    public void ResolveRevalidationInterval_DefaultsToFiveMinutes_WhenUnset()
    {
        var interval = TenantCircuitHandler.ResolveRevalidationInterval(
            new ConfigurationBuilder().Build());

        Assert.Equal(TimeSpan.FromMinutes(5), interval);
        Assert.Equal(TenantResolutionConstants.DefaultRevalidationInterval, interval);
    }

    [Theory]
    [InlineData("00:00:00")]   // zero would silently disable the guarantee
    [InlineData("-00:05:00")]  // negative likewise
    [InlineData("00:00:01")]   // below the floor
    public void ResolveRevalidationInterval_ClampsZeroNegativeAndTinyValues(string configured)
    {
        var interval = TenantCircuitHandler.ResolveRevalidationInterval(BuildConfiguration(configured));

        Assert.Equal(TenantResolutionConstants.MinimumRevalidationInterval, interval);
        Assert.True(interval > TimeSpan.Zero);
    }

    [Fact]
    public void ResolveRevalidationInterval_HonoursAValueAboveTheFloor()
    {
        var interval = TenantCircuitHandler.ResolveRevalidationInterval(BuildConfiguration("00:10:00"));

        Assert.Equal(TimeSpan.FromMinutes(10), interval);
    }

    [Theory]
    [InlineData("not-a-timespan")]
    [InlineData("")]
    public void ResolveRevalidationInterval_FallsBackToTheDefault_WhenUnparseable(string configured)
    {
        var interval = TenantCircuitHandler.ResolveRevalidationInterval(BuildConfiguration(configured));

        Assert.Equal(TenantResolutionConstants.DefaultRevalidationInterval, interval);
    }

    // ------------------------------------------------------------------- circuit lifecycle

    [Fact]
    public async Task CircuitLifecycle_StartsOneLoop_AndStopsItOnClose()
    {
        var (handler, _) = await OpenCircuitAsync(new FakeTenantRegistry(), AcmeId, "acme", UserId,
            closeCircuit: false);

        var loop = handler.RevalidationLoopTask;
        Assert.NotNull(loop);
        Assert.False(loop!.IsCompleted);

        // A reconnect must not stack a second loop.
        await handler.OnConnectionUpAsync(null!, CancellationToken.None);
        Assert.Same(loop, handler.RevalidationLoopTask);

        await handler.OnCircuitClosedAsync(null!, CancellationToken.None);

        // No task or timer outlives the circuit.
        Assert.NotNull(handler.RevalidationLoopTask);
        Assert.True(handler.RevalidationLoopTask!.IsCompleted);
    }

    [Fact]
    public async Task CircuitLifecycle_PausesTheLoopWhileDisconnected_AndResumesOnReconnect()
    {
        var (handler, _) = await OpenCircuitAsync(new FakeTenantRegistry(), AcmeId, "acme", UserId,
            closeCircuit: false);

        var first = handler.RevalidationLoopTask;
        Assert.NotNull(first);

        await handler.OnConnectionDownAsync(null!, CancellationToken.None);
        Assert.True(first!.IsCompleted);

        await handler.OnConnectionUpAsync(null!, CancellationToken.None);
        var second = handler.RevalidationLoopTask;
        Assert.NotNull(second);
        Assert.NotSame(first, second);
        Assert.False(second!.IsCompleted);

        await handler.OnCircuitClosedAsync(null!, CancellationToken.None);
        Assert.True(second.IsCompleted);
    }

    [Fact]
    public async Task CircuitLifecycle_WithNoSeededTenant_DoesNotStartALoop()
    {
        var state = new TenantState();
        var handler = CreateHandler(state, new FakeTenantRegistry(), UserId, tenantId: null);

        await handler.OnCircuitOpenedAsync(null!, CancellationToken.None);

        Assert.Null(handler.RevalidationLoopTask);
    }

    // ------------------------------------------------------------------------- helpers

    private static async Task<(TenantCircuitHandler Handler, TenantState State)> OpenCircuitAsync(
        FakeTenantRegistry registry,
        Guid tenantId,
        string slug,
        Guid? userId,
        bool closeCircuit = true)
    {
        var state = new TenantState();
        var handler = CreateHandler(state, registry, userId, tenantId, slug);

        await handler.OnCircuitOpenedAsync(null!, CancellationToken.None);

        if (closeCircuit)
        {
            // The decision tests need no timer; stop it before driving the seam directly.
            await handler.OnCircuitClosedAsync(null!, CancellationToken.None);
        }

        return (handler, state);
    }

    private static TenantCircuitHandler CreateHandler(
        TenantState state,
        FakeTenantRegistry registry,
        Guid? userId,
        Guid? tenantId,
        string slug = "acme")
    {
        var context = new DefaultHttpContext();

        var claims = new List<Claim>();
        if (userId is { } id)
        {
            claims.Add(new Claim(ClaimTypes.NameIdentifier, id.ToString()));
        }

        context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));

        if (tenantId is { } tid && tid != Guid.Empty)
        {
            context.Items[TenantResolutionConstants.HttpContextItemKey] =
                new TenantDescriptor(tid, slug, "Acme", "acentra_" + slug);
        }

        return new TenantCircuitHandler(
            state,
            new HttpContextAccessor { HttpContext = context },
            registry,
            BuildConfiguration(interval: null),
            NullLogger<TenantCircuitHandler>.Instance);
    }

    private static IConfiguration BuildConfiguration(string? interval)
    {
        var values = new Dictionary<string, string?>();

        if (interval is not null)
        {
            values[TenantResolutionConstants.RevalidationIntervalKey] = interval;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static TenantDescriptor Descriptor(Guid id, string slug) =>
        new(id, slug, slug, "acentra_" + slug);

    private sealed class FakeTenantRegistry : ITenantRegistry
    {
        public IReadOnlyList<TenantDescriptor> Memberships { get; set; } = [];

        public TenantDescriptor? TenantBySlug { get; set; }

        public Exception? Failure { get; set; }

        public int FindForUserCallCount { get; private set; }

        public int FindBySlugCallCount { get; private set; }

        public Task<IReadOnlyList<TenantDescriptor>> FindForUserAsync(Guid userId, CancellationToken ct)
        {
            FindForUserCallCount++;

            return Failure is null
                ? Task.FromResult(Memberships)
                : Task.FromException<IReadOnlyList<TenantDescriptor>>(Failure);
        }

        public Task<TenantDescriptor?> FindBySlugAsync(string slug, CancellationToken ct)
        {
            FindBySlugCallCount++;

            return Failure is null
                ? Task.FromResult(TenantBySlug)
                : Task.FromException<TenantDescriptor?>(Failure);
        }
    }
}
