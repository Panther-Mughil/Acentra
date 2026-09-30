using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Acentra.Domain.Abstractions;
using Acentra.Domain.Entities;
using Acentra.Infrastructure.ControlPlane;
using Acentra.Infrastructure.ControlPlane.Configurations;
using Acentra.Infrastructure.TenantData;
using Acentra.Web.Auth;
using Acentra.Web.Middleware;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Acentra.IntegrationTests;

/// <summary>
/// Drives the REQ-008 resolution cases through the REAL pipeline (DI, auth, middleware order,
/// routing) via <see cref="WebApplicationFactory{TEntryPoint}"/>, against the dev control-plane
/// database. A test authentication scheme stands in for the Identity cookie so the middle of the
/// matrix can be exercised without scripting an antiforgery login round-trip. The anti-enumeration
/// assertions compare full response bodies.
/// </summary>
public sealed class TenantResolutionPipelineTests : IClassFixture<TenantTestFactory>
{
    private readonly TenantTestFactory _factory;

    public TenantResolutionPipelineTests(TenantTestFactory factory) => _factory = factory;

    private static async Task AssertStatusAsync(HttpStatusCode expected, HttpResponseMessage response)
    {
        if (response.StatusCode != expected)
        {
            var body = await response.Content.ReadAsStringAsync();
            var detail = body.Length > 1500 ? body[..1500] : body;
            Assert.Fail($"Expected {(int)expected} {expected} but got {(int)response.StatusCode}. Body: {detail}");
        }
    }

    /// <summary>
    /// Asserts the middleware did NOT fail closed. The invariant these pipeline cases protect is that
    /// a valid member with a resolvable tenant is handed to routing; whether routing then renders a
    /// page (200) or finds no route (404) is routing's business and changes as pages are added. Only
    /// the uniform fail-closed statuses — 400 for a missing hint, 403 for a denial — mean the
    /// middleware blocked. Asserting a bare 404 would wrongly turn a legitimate page into a failure.
    /// </summary>
    private static void AssertNotBlocked(HttpResponseMessage response) =>
        Assert.True(
            response.StatusCode is not (HttpStatusCode.BadRequest or HttpStatusCode.Forbidden),
            "Expected the middleware to hand the request to routing, but it failed closed with " +
            $"{(int)response.StatusCode} {response.StatusCode}.");

    [Fact]
    public async Task Member_WithHeader_ResolvesAndContinuesToRouting()
    {
        var demoId = await GetDemoUserIdAsync();
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User", demoId.ToString());
        client.DefaultRequestHeaders.Add(TenantResolutionConstants.HeaderName, TenantSeeder.AcmeSlug);

        // The middleware authorized the request and handed it to routing. The route's own status is
        // routing's business (a tenant-scoped page now renders 200; an unrouted path is 404) — what
        // must never happen is the middleware failing closed with 400/403.
        var response = await client.GetAsync("/inventory");

        AssertNotBlocked(response);
    }

    [Fact]
    public async Task NonMember_WithHeader_IsForbidden()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User", Guid.NewGuid().ToString());
        client.DefaultRequestHeaders.Add(TenantResolutionConstants.HeaderName, TenantSeeder.AcmeSlug);

        var response = await client.GetAsync("/inventory");

        await AssertStatusAsync(HttpStatusCode.Forbidden, response);
    }

    [Fact]
    public async Task UnknownSlug_IsForbidden_WithTheUniformBody()
    {
        var demoId = await GetDemoUserIdAsync();
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User", demoId.ToString());
        client.DefaultRequestHeaders.Add(TenantResolutionConstants.HeaderName, "does-not-exist");

        var response = await client.GetAsync("/inventory");

        await AssertStatusAsync(HttpStatusCode.Forbidden, response);
        Assert.Equal(TenantResolutionConstants.AccessDeniedMessage, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task NoHint_OnTenantScopedPath_IsBadRequest()
    {
        var demoId = await GetDemoUserIdAsync();
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User", demoId.ToString());

        var response = await client.GetAsync("/inventory");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task NoHint_OnTenantAgnosticPath_Renders()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task AccountLoginPage_IsServed_StaticSsr()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/Account/Login");

        await AssertStatusAsync(HttpStatusCode.OK, response);
    }

    // ------------------------------------------------ §6 anti-enumeration (real pipeline)

    [Fact]
    public async Task Unauthenticated_ValidSlug_And_UnknownSlug_AreByteIdentical()
    {
        // AllowAutoRedirect=false so the Identity challenge redirect (302, empty body) is observed
        // rather than followed to the login page.
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        // A tenant-scoped, non-/api path. Unauthenticated, the middleware never reads the hint and
        // hands off to the Identity challenge (a redirect with an empty body). The two responses are
        // therefore byte-identical: nothing about tenant existence leaks. (/api/* is asserted at the
        // middleware boundary in TenantResolutionTests, where its bare 401 body is the middleware's own.)
        using var valid = await GetAsync(client, "/inventory", TenantSeeder.AcmeSlug);
        using var unknown = await GetAsync(client, "/inventory", "does-not-exist");

        Assert.Equal(HttpStatusCode.Redirect, valid.StatusCode);
        Assert.Equal(valid.Headers.Location, unknown.Headers.Location);
        await AssertResponsesByteIdenticalAsync(valid, unknown);
    }

    [Fact]
    public async Task Authenticated_Unknown_Suspended_And_NonMember_AreByteIdentical()
    {
        var demoId = await GetDemoUserIdAsync();
        var tempSlug = "req008-susp-" + Guid.NewGuid().ToString("N")[..8];
        var tempTenantId = await SeedTenantAsync(tempSlug, TenantStatus.Active, demoId);

        try
        {
            using var client = _factory.CreateClient();

            // Suspend it between seeding and the solicitation.
            await SetStatusAsync(tempSlug, TenantStatus.Suspended);

            HttpResponseMessage unknown;
            using (var request = new HttpRequestMessage(HttpMethod.Get, "/inventory"))
            {
                request.Headers.Add("X-Test-User", demoId.ToString());
                request.Headers.Add(TenantResolutionConstants.HeaderName, "req008-does-not-exist");
                unknown = await client.SendAsync(request);
            }

            HttpResponseMessage suspended;
            using (var request = new HttpRequestMessage(HttpMethod.Get, "/inventory"))
            {
                request.Headers.Add("X-Test-User", demoId.ToString());
                request.Headers.Add(TenantResolutionConstants.HeaderName, tempSlug);
                suspended = await client.SendAsync(request);
            }

            HttpResponseMessage nonMember;
            using (var request = new HttpRequestMessage(HttpMethod.Get, "/inventory"))
            {
                request.Headers.Add("X-Test-User", Guid.NewGuid().ToString());
                request.Headers.Add(TenantResolutionConstants.HeaderName, TenantSeeder.AcmeSlug);
                nonMember = await client.SendAsync(request);
            }

            Assert.Equal(HttpStatusCode.Forbidden, unknown.StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, suspended.StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, nonMember.StatusCode);

            await AssertResponsesByteIdenticalAsync(unknown, suspended);
            await AssertResponsesByteIdenticalAsync(unknown, nonMember);

            // The denial body must never contain the supplied slug.
            var body = await unknown.Content.ReadAsStringAsync();
            Assert.DoesNotContain(tempSlug, body, StringComparison.Ordinal);
        }
        finally
        {
            await DeleteTenantAsync(tempTenantId, TenantDatabaseName.FromSlug(tempSlug));
        }
    }

    // ----------------------------------------------------- §6 cache removal (real DB)

    [Fact]
    public async Task NewTenant_ResolvesImmediately_AndSuspendedStopsImmediately()
    {
        var demoId = await GetDemoUserIdAsync();
        var slug = "req008-live-" + Guid.NewGuid().ToString("N")[..8];
        var tenantId = await SeedTenantAsync(slug, TenantStatus.Active, demoId);

        try
        {
            using var client = _factory.CreateClient();

            // 1. The newly created tenant resolves on the *immediately next* request: no negative
            //    cache makes it wait, so the middleware hands the request to routing (not 400/403).
            using (var request = new HttpRequestMessage(HttpMethod.Get, "/inventory"))
            {
                request.Headers.Add("X-Test-User", demoId.ToString());
                request.Headers.Add(TenantResolutionConstants.HeaderName, slug);
                using var response = await client.SendAsync(request);
                AssertNotBlocked(response);
            }

            // 2. Flip to suspended. The *immediately next* request must fail closed: no positive
            //    cache keeps a suspended tenant alive.
            await SetStatusAsync(slug, TenantStatus.Suspended);

            using (var request = new HttpRequestMessage(HttpMethod.Get, "/inventory"))
            {
                request.Headers.Add("X-Test-User", demoId.ToString());
                request.Headers.Add(TenantResolutionConstants.HeaderName, slug);
                using var response = await client.SendAsync(request);
                await AssertStatusAsync(HttpStatusCode.Forbidden, response);
            }
        }
        finally
        {
            await DeleteTenantAsync(tenantId, TenantDatabaseName.FromSlug(slug));
        }
    }

    // -------------------------------------------------------- §6 allowlist / health

    [Fact]
    public async Task Health_NowRequiresATenant()
    {
        var demoId = await GetDemoUserIdAsync();
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User", demoId.ToString());

        // Allowlisted before REQ-008; now tenant-scoped, so an authenticated request with no hint
        // fails closed with 400 instead of rendering a (nonexistent) health page.
        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // -------------------------------------------------------------------- helpers

    private static async Task<HttpResponseMessage> GetAsync(HttpClient client, string path, string tenant)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add(TenantResolutionConstants.HeaderName, tenant);
        return await client.SendAsync(request);
    }

    private async Task<Guid> GetDemoUserIdAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var demo = await users.FindByEmailAsync(TenantSeeder.DemoUserEmail);

        Assert.NotNull(demo);
        return demo!.Id;
    }

    private async Task<Guid> SeedTenantAsync(string slug, string status, Guid memberId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();

        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Slug = slug,
            Name = "REQ-008 temp tenant",
            DatabaseName = TenantDatabaseName.FromSlug(slug),
            Status = status,
            CreatedUtc = DateTime.UtcNow
        };

        db.Tenants.Add(tenant);
        db.TenantMemberships.Add(new TenantMembership
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            UserId = memberId,
            Role = MembershipRole.Member
        });

        await db.SaveChangesAsync();
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

    private async Task DeleteTenantAsync(Guid tenantId, string databaseName)
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();

            var memberships = await db.TenantMemberships.Where(m => m.TenantId == tenantId).ToListAsync();
            db.TenantMemberships.RemoveRange(memberships);

            var tenant = await db.Tenants.FindAsync(tenantId);
            if (tenant is not null)
            {
                db.Tenants.Remove(tenant);
            }

            await db.SaveChangesAsync();
        }

        // Deleting the row alone would orphan the database: another test host starting while the
        // row was briefly Active runs TenantDatabaseInitializer and provisions it. Drop whatever
        // was provisioned so the run leaves nothing behind (REQ-006 defends this the same way).
        await IsolationTestSupport.DropDatabaseAsync(databaseName);
    }

    private static async Task AssertResponsesByteIdenticalAsync(
        HttpResponseMessage expected, HttpResponseMessage actual)
    {
        Assert.Equal(expected.StatusCode, actual.StatusCode);
        Assert.Equal(expected.Content.Headers.ContentType?.ToString(), actual.Content.Headers.ContentType?.ToString());

        var expectedBody = await expected.Content.ReadAsByteArrayAsync();
        var actualBody = await actual.Content.ReadAsByteArrayAsync();

        Assert.Equal(expectedBody, actualBody);
    }
}

/// <summary>
/// A <see cref="WebApplicationFactory{TEntryPoint}"/> whose default authentication scheme is the
/// header-driven <see cref="TenantTestAuth"/> handler.
/// </summary>
public sealed class TenantTestFactory : WebApplicationFactory<TenantResolutionMiddleware>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        TestDatabase.ApplyPoolCap(builder);
        builder.ConfigureTestServices(TenantTestAuth.Register);
    }
}

/// <summary>
/// Proves §4.6: with <c>ForwardedHeaders:Enabled=true</c> and <c>X-Forwarded-Proto: https</c>, the
/// continuity cookie is emitted with <c>Secure</c> even though the test server itself is plain HTTP.
/// </summary>
public sealed class ForwardedHeadersCookieTests : IClassFixture<ForwardedHeadersCookieTests.Factory>
{
    private readonly Factory _factory;

    public ForwardedHeadersCookieTests(Factory factory) => _factory = factory;

    [Fact]
    public async Task ForwardedHttps_MakesTheContinuityCookieSecure()
    {
        using var scope = _factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var demo = await users.FindByEmailAsync(TenantSeeder.DemoUserEmail);
        Assert.NotNull(demo);

        // Guard: the config gate must actually be on for this host.
        Assert.Equal(
            "true",
            _factory.Services.GetRequiredService<IConfiguration>()["ForwardedHeaders:Enabled"]);

        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/inventory");
        request.Headers.Add("X-Test-User", demo!.Id.ToString());
        request.Headers.Add(TenantResolutionConstants.HeaderName, TenantSeeder.AcmeSlug);
        request.Headers.Add("X-Forwarded-Proto", "https");
        request.Headers.Add("X-Forwarded-For", "203.0.113.7");

        using var response = await client.SendAsync(request);

        Assert.True(response.Headers.TryGetValues("Set-Cookie", out var setCookies));
        var tenantCookies = setCookies!
            .Where(c => c.StartsWith(TenantResolutionConstants.CookieName, StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(tenantCookies);
        Assert.All(tenantCookies, cookie =>
            Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase));
    }

    public sealed class Factory : WebApplicationFactory<TenantResolutionMiddleware>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            TestDatabase.ApplyPoolCap(builder);

            // UseSetting (host configuration) is present when Program reads builder.Configuration,
            // unlike a ConfigureAppConfiguration callback which can be applied after startup.
            builder.UseSetting("ForwardedHeaders:Enabled", "true");

            builder.ConfigureTestServices(services =>
            {
                TenantTestAuth.Register(services);

                // The test server is not loopback, so trust it explicitly. Production keeps the
                // default loopback-only known proxies/networks ("no unknown-proxy trust").
                services.Configure<ForwardedHeadersOptions>(options =>
                {
                    options.KnownIPNetworks.Clear();
                    options.KnownProxies.Clear();
                });
            });
        }
    }
}

/// <summary>
/// The shared header-driven test authentication scheme. A request with <c>X-Test-User</c> is
/// authenticated as that (possibly non-existent) user id; the header is absent for anonymous calls.
/// </summary>
internal static class TenantTestAuth
{
    public const string SchemeName = "TestTenantAuth";

    public static void Register(IServiceCollection services) =>
        services.AddAuthentication(options =>
            {
                options.DefaultScheme = SchemeName;
                options.DefaultAuthenticateScheme = SchemeName;
                // Use the real Identity cookie handler to challenge, so an unauthenticated request to a
                // tenant-scoped path is the production redirect (302, empty body) rather than the test
                // scheme's bare 401 that the status-code re-execution would turn into an HTML page.
                options.DefaultChallengeScheme = IdentityConstants.ApplicationScheme;
            })
            .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(SchemeName, _ => { });

    private sealed class TestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var userId = Request.Headers["X-Test-User"].ToString();

            if (string.IsNullOrWhiteSpace(userId))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var identity = new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, userId)],
                SchemeName);

            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
        }
    }
}
