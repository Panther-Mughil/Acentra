using System.Security.Claims;
using Acentra.Domain.Abstractions;
using Acentra.Domain.Entities;
using Acentra.Infrastructure.ControlPlane;
using Acentra.Infrastructure.ControlPlane.Configurations;
using Acentra.Infrastructure.TenantData;
using Acentra.Web.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Acentra.IntegrationTests;

/// <summary>
/// REQ-006 boundary B12 — authorization expiry on an already-open Blazor circuit — proven against
/// the <em>real</em> control plane, not a hand-rolled fake.
///
/// <para>
/// This is the integration counterpart to the unit-level <c>TenantCircuitHandlerTests</c>: it drives
/// the same committed seam (<see cref="TenantCircuitHandler.RevalidateAsync"/>) but resolves
/// memberships, tenant status and the circuit seed from real <c>Tenant</c>/<c>TenantMembership</c>
/// rows through the production <see cref="TenantRegistry"/>. Every temporary tenant is GUID-suffixed
/// and removed (with any database a concurrently starting test host may have provisioned) in
/// <c>finally</c>, so the developer's real <c>acme</c>/<c>globex</c> rows are never touched and the
/// suite leaves nothing behind.
/// </para>
///
/// <para>
/// The hole being closed: with interactive Server a circuit makes no per-interaction HTTP request,
/// so the middleware authorizes once at the <c>/_blazor</c> handshake and never again. Without
/// revalidation a revoked membership or a suspended tenant kept working for as long as the tab
/// stayed open. The six checks below are the decision table, fail-closed at every branch.
/// </para>
/// </summary>
public sealed class B12CircuitRevalidationBoundaryTests
{
    private static readonly string[] DemoRealTenantSlugs = [TenantSeeder.AcmeSlug, TenantSeeder.GlobexSlug];

    // ------------------------------------------------------------------ item 1: seeding is real

    [Fact]
    public async Task B12_1_CircuitSeedIsReal_TheTenantIsResolvedFromTheHandshakeBeforeAnyRevocation()
    {
        await using var db = OpenControlPlane();
        var registry = RegistryOver(db);
        var tracked = new List<Guid>();

        try
        {
            var demoId = await DemoUserIdAsync(db);
            var tenant = await SeedTenantAsync(db, tracked, "seeded", demoId);

            var (handler, state) = await OpenCircuitAsync(registry, tenant.Id, tenant.Slug, demoId);

            // The anti-vacuity guard: the circuit really did seed a tenant, so a later "cleared"
            // cannot be the result of it having been empty to begin with.
            Assert.True(state.IsResolved);
            Assert.Equal(tenant.Id, state.TenantId);
            Assert.Equal(tenant.Slug, state.Slug);

            // And the real registry agrees this circuit is still authorized.
            Assert.True(await handler.RevalidateAsync(CancellationToken.None));
            Assert.True(state.IsResolved);
            Assert.Equal(tenant.Id, state.TenantId);
        }
        finally
        {
            await CleanupAsync(db, tracked);
        }
    }

    // ------------------------------------------------------------- item 2: revoked membership

    [Fact]
    public async Task B12_2_RevokedMembership_ClearsTheCircuit_ReturnsFalse_AndRaisesChangedExactlyOnce()
    {
        await using var db = OpenControlPlane();
        var registry = RegistryOver(db);
        var tracked = new List<Guid>();

        try
        {
            var demoId = await DemoUserIdAsync(db);
            var tenant = await SeedTenantAsync(db, tracked, "revoked", demoId);

            var (handler, state) = await OpenCircuitAsync(registry, tenant.Id, tenant.Slug, demoId);

            // Before: the circuit really is authorized to this tenant.
            Assert.True(state.IsResolved);
            Assert.Equal(tenant.Id, state.TenantId);

            // The revocation: the membership row is removed from the control plane.
            await RemoveMembershipAsync(db, tenant.Id, demoId);

            var changed = 0;
            state.Changed += () => changed++;

            var result = await handler.RevalidateAsync(CancellationToken.None);

            Assert.False(result);
            Assert.False(state.IsResolved);
            Assert.Equal(Guid.Empty, state.TenantId);
            Assert.Equal(string.Empty, state.Slug);

            // TenantState.Clear() is the single expiry path, so Changed fires exactly once.
            Assert.Equal(1, changed);
        }
        finally
        {
            await CleanupAsync(db, tracked);
        }
    }

    // ------------------------------------------------- item 3: suspended (or unknown) tenant

    [Fact]
    public async Task B12_3_SuspendedTenant_NotMembership_ClearsTheCircuit_AndRaisesChangedExactlyOnce()
    {
        await using var db = OpenControlPlane();
        var registry = RegistryOver(db);
        var tracked = new List<Guid>();

        try
        {
            var demoId = await DemoUserIdAsync(db);
            var tenant = await SeedTenantAsync(db, tracked, "suspended", demoId);

            var (handler, state) = await OpenCircuitAsync(registry, tenant.Id, tenant.Slug, demoId);

            Assert.True(state.IsResolved);

            // The membership stays intact; only the tenant's lifecycle status changes.
            await SetStatusAsync(db, tenant.Id, TenantStatus.Suspended);

            var changed = 0;
            state.Changed += () => changed++;

            var result = await handler.RevalidateAsync(CancellationToken.None);

            Assert.False(result);
            Assert.False(state.IsResolved);

            // Exactly one Changed, as with a revoked membership: the cause must not change the
            // clearing behaviour.
            Assert.Equal(1, changed);
        }
        finally
        {
            await CleanupAsync(db, tracked);
        }
    }

    // ------------------------------------------------------- item 4: the valid path is a no-op

    [Fact]
    public async Task B12_4_ValidMembership_LeavesTheCircuitCompletelyUntouched_WithNoChangedEvent()
    {
        await using var db = OpenControlPlane();
        var registry = RegistryOver(db);
        var tracked = new List<Guid>();

        try
        {
            var demoId = await DemoUserIdAsync(db);
            var tenant = await SeedTenantAsync(db, tracked, "valid", demoId);

            var (handler, state) = await OpenCircuitAsync(registry, tenant.Id, tenant.Slug, demoId);

            Assert.True(state.IsResolved);

            var changed = 0;
            state.Changed += () => changed++;

            // Two ticks, to prove idempotence rather than a lucky first call.
            Assert.True(await handler.RevalidateAsync(CancellationToken.None));
            Assert.True(await handler.RevalidateAsync(CancellationToken.None));

            Assert.True(state.IsResolved);
            Assert.Equal(tenant.Id, state.TenantId);
            Assert.Equal(tenant.Slug, state.Slug);

            // A spurious Changed here would wipe every page's rows on every interval — a real
            // requirement, not an implementation detail.
            Assert.Equal(0, changed);
        }
        finally
        {
            await CleanupAsync(db, tracked);
        }
    }

    // ------------------------------------------------------------ item 5: clear, never switch

    [Fact]
    public async Task B12_5_WhenTheUserStillBelongsToADifferentTenant_TheCircuitIsCleared_NotRetargeted()
    {
        await using var db = OpenControlPlane();
        var registry = RegistryOver(db);
        var tracked = new List<Guid>();

        try
        {
            var demoId = await DemoUserIdAsync(db);

            // The user belongs to BOTH temporary tenants; only this circuit's tenant is revoked.
            var circuitTenant = await SeedTenantAsync(db, tracked, "never-switch-a", demoId);
            var otherTenant = await SeedTenantAsync(db, tracked, "never-switch-b", demoId);

            var (handler, state) = await OpenCircuitAsync(registry, circuitTenant.Id, circuitTenant.Slug, demoId);

            Assert.True(state.IsResolved);
            Assert.Equal(circuitTenant.Id, state.TenantId);

            await RemoveMembershipAsync(db, circuitTenant.Id, demoId);

            var changed = 0;
            state.Changed += () => changed++;

            var result = await handler.RevalidateAsync(CancellationToken.None);

            Assert.False(result);
            Assert.False(state.IsResolved);
            Assert.Equal(Guid.Empty, state.TenantId);

            // The other tenant is still available to the user — but the circuit must never adopt it.
            Assert.NotEqual(otherTenant.Id, state.TenantId);
            Assert.NotEqual(otherTenant.Slug, state.Slug);

            // Nor the developer's real tenants, which the demo user also belongs to.
            foreach (var slug in DemoRealTenantSlugs)
            {
                Assert.NotEqual(slug, state.Slug);
            }

            Assert.Equal(1, changed);
        }
        finally
        {
            await CleanupAsync(db, tracked);
        }
    }

    // ----------------------------------------------------------- item 6: fail closed on error

    [Fact]
    public async Task B12_6_WhenTheRegistryThrows_TheCircuitClears_InsteadOfTreatingTheErrorAsValid()
    {
        var tenantId = Guid.NewGuid();
        const string slug = "b12-registry-error";

        var (handler, state) = await OpenCircuitAsync(
            new ThrowingTenantRegistry(),
            tenantId,
            slug,
            userId: Guid.NewGuid());

        // Seeded from the handshake, so the failure below is a real clear, not a no-op on empty state.
        Assert.True(state.IsResolved);
        Assert.Equal(tenantId, state.TenantId);

        var changed = 0;
        state.Changed += () => changed++;

        var result = await handler.RevalidateAsync(CancellationToken.None);

        Assert.False(result);
        Assert.False(state.IsResolved);
        Assert.Equal(Guid.Empty, state.TenantId);
        Assert.Equal(1, changed);
    }

    // --------------------------------------------------------------------------------- helpers

    private static ControlPlaneDbContext OpenControlPlane()
    {
        var connectionString = IsolationTestSupport.Configuration.GetConnectionString("ControlPlane");

        var options = new DbContextOptionsBuilder<ControlPlaneDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new ControlPlaneDbContext(options);
    }

    private static TenantRegistry RegistryOver(ControlPlaneDbContext db) =>
        new(db, NullLogger<TenantRegistry>.Instance);

    private static async Task<Guid> DemoUserIdAsync(ControlPlaneDbContext db)
    {
        var demo = await db.Users.SingleOrDefaultAsync(user => user.Email == TenantSeeder.DemoUserEmail);

        Assert.NotNull(demo);
        return demo!.Id;
    }

    /// <summary>
    /// Seeds a GUID-suffixed temporary tenant (and, when a member is supplied, a membership for it)
    /// and records it for teardown. Active on purpose: item 3 suspends it, item 2/5 revoke it.
    /// </summary>
    private static async Task<TenantDescriptor> SeedTenantAsync(
        ControlPlaneDbContext db,
        List<Guid> tracked,
        string label,
        Guid? memberId,
        string status = TenantStatus.Active)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var slug = $"b12-{label}-{suffix}";
        var databaseName = TenantDatabaseName.Normalize($"acentra_b12_{label}_{suffix}");

        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Slug = slug,
            Name = $"B12 {label}",
            DatabaseName = databaseName,
            Status = status,
            CreatedUtc = DateTime.UtcNow
        };

        db.Tenants.Add(tenant);

        if (memberId is { } userId)
        {
            db.TenantMemberships.Add(new TenantMembership
            {
                Id = Guid.NewGuid(),
                TenantId = tenant.Id,
                UserId = userId,
                Role = MembershipRole.Member
            });
        }

        await db.SaveChangesAsync();

        tracked.Add(tenant.Id);
        return new TenantDescriptor(tenant.Id, slug, tenant.Name, databaseName);
    }

    private static async Task RemoveMembershipAsync(ControlPlaneDbContext db, Guid tenantId, Guid userId)
    {
        var membership = await db.TenantMemberships
            .SingleAsync(m => m.TenantId == tenantId && m.UserId == userId);

        db.TenantMemberships.Remove(membership);
        await db.SaveChangesAsync();
    }

    private static async Task SetStatusAsync(ControlPlaneDbContext db, Guid tenantId, string status)
    {
        var tenant = await db.Tenants.SingleAsync(t => t.Id == tenantId);

        tenant.Status = status;
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Removes every tracked control-plane row and drops any database a concurrently starting test
    /// host's <c>TenantDatabaseInitializer</c> may have provisioned while the tenant was Active.
    /// </summary>
    private static async Task CleanupAsync(ControlPlaneDbContext db, IEnumerable<Guid> tracked)
    {
        foreach (var tenantId in tracked)
        {
            string? databaseName = null;

            var tenant = await db.Tenants.FindAsync(tenantId);

            if (tenant is not null)
            {
                databaseName = tenant.DatabaseName;

                var memberships = await db.TenantMemberships
                    .Where(m => m.TenantId == tenantId)
                    .ToListAsync();

                db.TenantMemberships.RemoveRange(memberships);
                db.Tenants.Remove(tenant);
                await db.SaveChangesAsync();
            }

            if (databaseName is not null)
            {
                await IsolationTestSupport.DropDatabaseAsync(databaseName);
            }
        }
    }

    /// <summary>
    /// Opens a real circuit through the committed seam: seeds <see cref="TenantState"/> from the
    /// handshake <c>HttpContext</c> exactly as <c>TenantCircuitHandler</c> does in production, then
    /// stops the background loop (the decision tests drive <see cref="TenantCircuitHandler.RevalidateAsync"/>
    /// directly and must not leave a timer running).
    /// </summary>
    private static async Task<(TenantCircuitHandler Handler, TenantState State)> OpenCircuitAsync(
        ITenantRegistry registry,
        Guid tenantId,
        string slug,
        Guid? userId)
    {
        var state = new TenantState();
        var context = new DefaultHttpContext();

        var claims = new List<Claim>();
        if (userId is { } id)
        {
            claims.Add(new Claim(ClaimTypes.NameIdentifier, id.ToString()));
        }

        context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
        context.Items[TenantResolutionConstants.HttpContextItemKey] =
            new TenantDescriptor(tenantId, slug, slug, TenantDatabaseName.FromSlug(slug));

        var handler = new TenantCircuitHandler(
            state,
            new HttpContextAccessor { HttpContext = context },
            registry,
            new ConfigurationBuilder().Build(),
            NullLogger<TenantCircuitHandler>.Instance);

        await handler.OnCircuitOpenedAsync(null!, CancellationToken.None);
        await handler.OnCircuitClosedAsync(null!, CancellationToken.None);

        return (handler, state);
    }

    /// <summary>An unreachable control plane: every lookup faults, so the handler must fail closed.</summary>
    private sealed class ThrowingTenantRegistry : ITenantRegistry
    {
        private static readonly InvalidOperationException Failure =
            new("control-plane database unreachable");

        public Task<TenantDescriptor?> FindBySlugAsync(string slug, CancellationToken ct) =>
            Task.FromException<TenantDescriptor?>(Failure);

        public Task<IReadOnlyList<TenantDescriptor>> FindForUserAsync(Guid userId, CancellationToken ct) =>
            Task.FromException<IReadOnlyList<TenantDescriptor>>(Failure);
    }
}
