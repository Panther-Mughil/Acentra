using System.Net;
using System.Security.Claims;
using Acentra.Domain.Abstractions;
using Acentra.Domain.Entities;
using Acentra.Infrastructure.ControlPlane;
using Acentra.Infrastructure.ControlPlane.Configurations;
using Acentra.Infrastructure.TenantData;
using Acentra.Web.Auth;
using Acentra.Web.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Acentra.IntegrationTests;

/// <summary>
/// REQ-006 §4 boundaries B4, B5, B6, B6b, B6c, B11 and B13 — the pipeline half of the isolation
/// matrix. Every case is driven through the real host (DI, authentication, middleware order and
/// routing) via the shared <see cref="TenantTestFactory"/>, whose header-driven authentication
/// stands in for the Identity cookie without weakening any production isolation path.
///
/// The probe path <c>/req006-isolation-probe</c> is deliberately not a real route: the middleware
/// decides before routing, so the assertion is about resolution, not the 200/404 that follows.
///
/// Every temporary tenant is tracked from the moment it is created and removed in <c>finally</c>, so
/// a mid-test failure can never leave an Active control-plane row behind (which a later host startup
/// would provision).
/// </summary>
public sealed class TenantIsolationBoundariesTests : IClassFixture<TenantTestFactory>
{
    private const string ScopedPath = "/req006-isolation-probe";

    private readonly TenantTestFactory _factory;

    public TenantIsolationBoundariesTests(TenantTestFactory factory) => _factory = factory;

    // ------------------------------------------------------------------------------- B4

    [Fact]
    public async Task B4_ForeignTenantRequest_ValidForOneTenantButRequestingAnother_IsForbidden()
    {
        var demoId = await DemoUserIdAsync();
        var tracked = new List<Guid>();

        try
        {
            // An ACTIVE tenant the caller is not a member of — a "foreign" tenant, not an unknown one.
            var foreignSlug = NewSlug("b4");
            await SeedAsync(tracked, foreignSlug, TenantStatus.Active);

            using var client = _factory.CreateClient();

            // Sanity control: the caller's credentials really are valid for its own tenant, so the
            // 403 below is specifically the foreign-tenant case, not an unusable identity.
            using (var own = await SendAsync(client, ScopedPath, demoId, TenantSeeder.AcmeSlug))
            {
                Assert.NotEqual(HttpStatusCode.Forbidden, own.StatusCode);
                Assert.NotEqual(HttpStatusCode.BadRequest, own.StatusCode);
            }

            using var foreign = await SendAsync(client, ScopedPath, demoId, foreignSlug);

            Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
            Assert.Equal(TenantResolutionConstants.AccessDeniedMessage, await foreign.Content.ReadAsStringAsync());
        }
        finally
        {
            await CleanupAsync(tracked);
        }
    }

    // ------------------------------------------------------------------------------- B5

    [Fact]
    public async Task B5_MissingHint_AuthenticatedOnTenantScopedPath_IsBadRequest()
    {
        var demoId = await DemoUserIdAsync();
        using var client = _factory.CreateClient();

        using var response = await SendAsync(client, ScopedPath, demoId, tenant: null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ------------------------------------------------------------------------------- B6

    [Fact]
    public async Task B6_UnknownTenant_IsForbidden_WithABodyByteIdenticalToForeignAndSuspended()
    {
        var demoId = await DemoUserIdAsync();
        var tracked = new List<Guid>();

        try
        {
            var suspendedSlug = NewSlug("b6susp");
            await SeedAsync(tracked, suspendedSlug, TenantStatus.Active, demoId);

            var foreignSlug = NewSlug("b6foreign");
            await SeedAsync(tracked, foreignSlug, TenantStatus.Active);

            await SetStatusAsync(suspendedSlug, TenantStatus.Suspended);
            using var client = _factory.CreateClient();

            using var unknown = await SendAsync(client, ScopedPath, demoId, "req006-does-not-exist");
            using var foreign = await SendAsync(client, ScopedPath, demoId, foreignSlug);
            using var suspended = await SendAsync(client, ScopedPath, demoId, suspendedSlug);

            Assert.Equal(HttpStatusCode.Forbidden, unknown.StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, suspended.StatusCode);

            // B6 must be byte-identical to both B4 (foreign) and B6b (suspended).
            await AssertByteIdenticalAsync(unknown, foreign);
            await AssertByteIdenticalAsync(unknown, suspended);
        }
        finally
        {
            await CleanupAsync(tracked);
        }
    }

    // ------------------------------------------------------------------------------ B6b

    [Fact]
    public async Task B6b_SuspendedTenant_IsForbidden_WithTheUniformSlugFreeBody()
    {
        var demoId = await DemoUserIdAsync();
        var tracked = new List<Guid>();

        try
        {
            var slug = NewSlug("b6b");
            await SeedAsync(tracked, slug, TenantStatus.Active, demoId);
            await SetStatusAsync(slug, TenantStatus.Suspended);

            using var client = _factory.CreateClient();

            // Positive control: the same caller still reaches its own ACTIVE seeded tenant.
            using (var active = await SendAsync(client, ScopedPath, demoId, TenantSeeder.AcmeSlug))
            {
                Assert.NotEqual(HttpStatusCode.Forbidden, active.StatusCode);
            }

            using var response = await SendAsync(client, ScopedPath, demoId, slug);

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

            var body = await response.Content.ReadAsStringAsync();
            Assert.Equal(TenantResolutionConstants.AccessDeniedMessage, body);
            Assert.DoesNotContain(slug, body, StringComparison.Ordinal);
        }
        finally
        {
            await CleanupAsync(tracked);
        }
    }

    // ------------------------------------------------------------------------------ B6c

    [Fact]
    public async Task B6c_UnauthenticatedProbing_EveryHintIsByteIdentical_AndPerformsNoTenantLookup()
    {
        var validSlug = TenantSeeder.AcmeSlug;
        var unknownSlug = "req006-b6c-" + Guid.NewGuid().ToString("N")[..8];
        var unregisteredSlug = NewSlug("b6c");
        string?[] hints = [null, validSlug, unknownSlug, unregisteredSlug];

        // (a) Real pipeline. Unauthenticated, the middleware never reads the hint: it hands straight
        //     off to the Identity challenge, so a valid slug, an unknown slug and no hint all produce
        //     one byte-identical redirect.
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var observed = new List<(string Label, int Status, string? ContentType, string? Location, byte[] Body)>();

        foreach (var hint in hints)
        {
            using var response = await SendUnauthenticatedAsync(client, ScopedPath, hint);

            observed.Add((
                hint ?? "<none>",
                (int)response.StatusCode,
                response.Content.Headers.ContentType?.ToString(),
                response.Headers.Location?.ToString(),
                await response.Content.ReadAsByteArrayAsync()));
        }

        var baseline = observed[0];
        Assert.Equal(302, baseline.Status); // the Identity challenge redirect — no tenant was resolved

        foreach (var snapshot in observed)
        {
            Assert.Equal(baseline.Status, snapshot.Status);
            Assert.Equal(baseline.ContentType, snapshot.ContentType);
            Assert.Equal(baseline.Location, snapshot.Location);
            Assert.Equal(baseline.Body, snapshot.Body); // full bytes, not just the status code
        }

        // (b) Direct middleware probe with a counting registry: because authentication is decided
        //     FIRST, no tenant lookup runs at all — so tenant existence cannot be probed anonymously.
        var registry = new CountingTenantRegistry();

        foreach (var hint in hints)
        {
            var (context, state) = BuildProbeContext(authenticated: false, hint);
            await InvokeMiddlewareAsync(context, state, registry);

            Assert.Equal(0, registry.FindBySlugCalls);
            Assert.Equal(0, registry.FindForUserCalls);
            Assert.False(state.IsResolved);
        }

        Assert.Equal(0, registry.FindBySlugCalls);
        Assert.Equal(0, registry.FindForUserCalls);
    }

    // ------------------------------------------------------------------------------ B11

    [Fact]
    public async Task B11_NoEnumerationOracle_UnknownSuspendedNonMemberAndForeignBodiesAreByteIdentical_AndLeakNoSlug()
    {
        var demoId = await DemoUserIdAsync();
        var tracked = new List<Guid>();

        try
        {
            var suspendedSlug = NewSlug("b11susp");
            await SeedAsync(tracked, suspendedSlug, TenantStatus.Active, demoId);

            var foreignSlug = NewSlug("b11foreign");
            await SeedAsync(tracked, foreignSlug, TenantStatus.Active);

            var unknownSlug = "req006-b11-" + Guid.NewGuid().ToString("N")[..8];

            await SetStatusAsync(suspendedSlug, TenantStatus.Suspended);
            using var client = _factory.CreateClient();

            using var unknown = await SendAsync(client, ScopedPath, demoId, unknownSlug);
            using var suspended = await SendAsync(client, ScopedPath, demoId, suspendedSlug);
            using var nonMember = await SendAsync(client, ScopedPath, Guid.NewGuid(), TenantSeeder.AcmeSlug);
            using var foreign = await SendAsync(client, ScopedPath, demoId, foreignSlug);

            Assert.Equal(HttpStatusCode.Forbidden, unknown.StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, suspended.StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, nonMember.StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);

            // Full response bodies, byte-for-byte — a status-only comparison would hide an oracle in
            // the body (a different message per cause).
            await AssertByteIdenticalAsync(unknown, suspended);
            await AssertByteIdenticalAsync(unknown, nonMember);
            await AssertByteIdenticalAsync(unknown, foreign);

            var body = await unknown.Content.ReadAsStringAsync();

            // The body never contains any slug that was supplied.
            foreach (var slug in new[] { unknownSlug, suspendedSlug, foreignSlug, TenantSeeder.AcmeSlug, TenantSeeder.GlobexSlug })
            {
                Assert.DoesNotContain(slug, body, StringComparison.OrdinalIgnoreCase);
            }
        }
        finally
        {
            await CleanupAsync(tracked);
        }
    }

    // ------------------------------------------------------------------------------ B13

    [Theory]
    [InlineData("/", "req006-b13-missing")]             // header hint, unknown tenant
    [InlineData("/", "globex")]                         // header hint, a real tenant the caller is NOT a member of
    [InlineData("/?tenant=req006-b13-missing", null)]   // query hint, unknown tenant
    public async Task B13_AllowlistedPath_ForeignOrUnknownHint_IsInert_NoForbiddenAndCookieUntouched(
        string path,
        string? header)
    {
        // Authenticated, but a member of nothing: neither hint can resolve, so the allowlisted path
        // must simply ignore it.
        var outsider = Guid.NewGuid();

        using var client = _factory.CreateClient();

        using var response = await SendAsync(
            client,
            path,
            outsider,
            header,
            continuityCookie: TenantSeeder.AcmeSlug); // a valid selection must not be wiped

        // No 403: the request rendered the tenant-agnostic page.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // The continuity cookie is not cleared by a hint failure on an allowlisted path.
        var setCookie = SetCookieOf(response);
        Assert.DoesNotContain(TenantResolutionConstants.CookieName, setCookie, StringComparison.Ordinal);

        // …and the uniform denial body never leaked into the page.
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(TenantResolutionConstants.AccessDeniedMessage, body, StringComparison.Ordinal);
    }

    // --------------------------------------------------------------------------- helpers

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        string path,
        Guid? user,
        string? tenant,
        string? continuityCookie = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);

        if (user is { } userId)
        {
            request.Headers.Add("X-Test-User", userId.ToString());
        }

        if (tenant is not null)
        {
            request.Headers.Add(TenantResolutionConstants.HeaderName, tenant);
        }

        if (continuityCookie is not null)
        {
            request.Headers.Add("Cookie", $"{TenantResolutionConstants.CookieName}={continuityCookie}");
        }

        return await client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> SendUnauthenticatedAsync(HttpClient client, string path, string? tenant) =>
        SendAsync(client, path, user: null, tenant: tenant);

    private static string SetCookieOf(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var values) ? string.Join("; ", values) : string.Empty;

    private static async Task AssertByteIdenticalAsync(HttpResponseMessage expected, HttpResponseMessage actual)
    {
        Assert.Equal(expected.StatusCode, actual.StatusCode);
        Assert.Equal(
            expected.Content.Headers.ContentType?.ToString(),
            actual.Content.Headers.ContentType?.ToString());
        Assert.Equal(
            await expected.Content.ReadAsByteArrayAsync(),
            await actual.Content.ReadAsByteArrayAsync());
    }

    private static (DefaultHttpContext Context, TenantState State) BuildProbeContext(bool authenticated, string? hint)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = new PathString("/api/products");
        context.Request.Host = new HostString("localhost");
        context.Response.Body = new MemoryStream();

        if (hint is not null)
        {
            context.Request.Headers[TenantResolutionConstants.HeaderName] = hint;
        }

        if (authenticated)
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())],
                authenticationType: "test"));
        }

        return (context, new TenantState());
    }

    private static async Task InvokeMiddlewareAsync(HttpContext context, TenantState state, ITenantRegistry registry)
    {
        var middleware = new TenantResolutionMiddleware(_ => Task.CompletedTask);

        ITenantResolver[] resolvers =
        [
            new HeaderTenantResolver(),
            new QueryTenantResolver(),
            new SubdomainTenantResolver(),
            new CookieTenantResolver()
        ];

        await middleware.InvokeAsync(
            context,
            resolvers,
            registry,
            state,
            new ProbeWebHostEnvironment(),
            NullLogger<TenantResolutionMiddleware>.Instance);
    }

    private async Task<Guid> DemoUserIdAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var demo = await users.FindByEmailAsync(TenantSeeder.DemoUserEmail);

        Assert.NotNull(demo);
        return demo!.Id;
    }

    /// <summary>
    /// Seeds a temporary tenant and immediately records it for teardown. A membership row is only
    /// added for a real user id (the FK to AspNetUsers is enforced); passing none leaves the tenant
    /// with no members, which is exactly the "foreign tenant" case.
    /// </summary>
    private async Task<Guid> SeedAsync(List<Guid> tracked, string slug, string status, Guid? memberId = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();

        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Slug = slug,
            Name = "REQ-006 temporary tenant",
            DatabaseName = TenantDatabaseName.FromSlug(slug),
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
        return tenant.Id;
    }

    private async Task SetStatusAsync(string slug, string status)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        var tenant = await db.Tenants.SingleAsync(t => t.Slug == slug);

        tenant.Status = status;
        await db.SaveChangesAsync();
    }

    /// <summary>Removes every tracked tenant row and drops any database a host startup may have created.</summary>
    private async Task CleanupAsync(IEnumerable<Guid> tracked)
    {
        foreach (var tenantId in tracked)
        {
            string? databaseName = null;

            using (var scope = _factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
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
            }

            // A concurrently-starting host's startup provisioner may have created the tenant database
            // because the row was (briefly) Active. Drop it so teardown leaves nothing behind.
            if (databaseName is not null)
            {
                await IsolationTestSupport.DropDatabaseAsync(databaseName);
            }
        }
    }

    private static string NewSlug(string label) =>
        $"req006-{label}-{Guid.NewGuid().ToString("N")[..8]}";

    private sealed class CountingTenantRegistry : ITenantRegistry
    {
        public int FindBySlugCalls { get; private set; }

        public int FindForUserCalls { get; private set; }

        public Task<TenantDescriptor?> FindBySlugAsync(string slug, CancellationToken ct)
        {
            FindBySlugCalls++;
            return Task.FromResult<TenantDescriptor?>(null);
        }

        public Task<IReadOnlyList<TenantDescriptor>> FindForUserAsync(Guid userId, CancellationToken ct)
        {
            FindForUserCalls++;
            return Task.FromResult<IReadOnlyList<TenantDescriptor>>([]);
        }
    }
}
