using System.Security.Claims;
using System.Text;
using Acentra.Domain.Abstractions;
using Acentra.Infrastructure.ControlPlane;
using Acentra.Web.Auth;
using Acentra.Web.Middleware;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;

namespace Acentra.IntegrationTests;

/// <summary>
/// Focused proof of the REQ-008 §6 resolution matrix (auth-first ordering, the uniform 403,
/// source-aware cookie handling and the tightened allowlist) and of the Blazor circuit bridge.
/// These drive <see cref="TenantResolutionMiddleware"/> and <see cref="TenantCircuitHandler"/>
/// directly with a real <see cref="TenantState"/>/<see cref="ITenantContext"/> and the real
/// hint resolvers, so the cases are asserted deterministically (no DB, no network, no SignalR
/// flakiness). The anti-enumeration assertions compare full response bodies, not just codes.
/// </summary>
public sealed class TenantResolutionTests
{
    private static readonly Guid AcmeId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid GlobexId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid MemberId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid OutsiderId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private static readonly TenantDescriptor Acme = new(AcmeId, "acme", "Acme Corporation", "acentra_acme");
    private static readonly TenantDescriptor Globex = new(GlobexId, "globex", "Globex Corporation", "acentra_globex");

    // ---------------------------------------------------------------- §6 cases

    [Fact]
    public async Task Member_WithHeader_ResolvesTenant()
    {
        var (context, state) = Build("/inventory", member: MemberId, header: "acme");
        var registry = RegistryAcmeFor(MemberId);

        var called = await InvokeAsync(context, state, registry);

        Assert.True(called);
        Assert.Equal(200, context.Response.StatusCode);
        Assert.True(state.IsResolved);
        Assert.Equal(AcmeId, state.TenantId);
        Assert.Equal("acme", state.Slug);
    }

    [Fact]
    public async Task NonMember_WithHeader_IsForbidden()
    {
        var (context, state) = Build("/inventory", member: OutsiderId, header: "acme");
        var registry = RegistryAcmeFor(MemberId);

        var called = await InvokeAsync(context, state, registry);

        Assert.False(called);
        Assert.Equal(403, context.Response.StatusCode);
        Assert.False(state.IsResolved);
    }

    [Fact]
    public async Task UnknownSlug_IsForbidden_WithTheUniformBody()
    {
        var (context, state) = Build("/inventory", member: MemberId, header: "does-not-exist");
        var registry = RegistryAcmeFor(MemberId);

        var called = await InvokeAsync(context, state, registry);

        Assert.False(called);
        Assert.Equal(403, context.Response.StatusCode);
        Assert.Equal(TenantResolutionConstants.AccessDeniedMessage, BodyText(context));
        Assert.False(state.IsResolved);
    }

    [Fact]
    public async Task NoHint_OnTenantScopedPath_IsBadRequest_AndUnresolved()
    {
        var (context, state) = Build("/inventory", member: MemberId, header: null);
        var registry = RegistryAcmeFor(MemberId);

        var called = await InvokeAsync(context, state, registry);

        Assert.False(called);
        Assert.Equal(400, context.Response.StatusCode);
        Assert.False(state.IsResolved);

        // Fail closed: an unresolved tenant must never mean "all tenants" / no filter.
        Assert.Equal(Guid.Empty, state.TenantId);
        Assert.Equal(string.Empty, state.Slug);
    }

    // ------------------------------------------------------------ fail-open guards

    [Fact]
    public async Task NoHint_OnTenantAgnosticPath_ContinuesUnresolved()
    {
        var (context, state) = Build("/", member: MemberId, header: null);
        var registry = RegistryAcmeFor(MemberId);

        var called = await InvokeAsync(context, state, registry);

        Assert.True(called);
        Assert.Equal(200, context.Response.StatusCode);
        Assert.False(state.IsResolved);
    }

    [Fact]
    public async Task Unauthenticated_ApiRequest_IsUnauthorized()
    {
        var (context, state) = Build("/api/products", member: null, header: "acme");
        var registry = RegistryAcmeFor(MemberId);

        var called = await InvokeAsync(context, state, registry);

        Assert.False(called);
        Assert.Equal(401, context.Response.StatusCode);
        Assert.False(state.IsResolved);
    }

    // ------------------------------------------------------------- hint precedence

    [Fact]
    public async Task Header_WinsOverSubdomain()
    {
        var (context, state) = Build("/inventory", member: MemberId, header: "acme");
        context.Request.Host = new HostString("globex.inventory.app");
        var registry = RegistryAcmeFor(MemberId);
        registry.BySlug["globex"] = Globex;

        await InvokeAsync(context, state, registry);

        Assert.Equal("acme", state.Slug);
    }

    [Fact]
    public async Task Subdomain_Resolves_WhenHostHasThreeLabels()
    {
        var (context, state) = Build("/inventory", member: MemberId, header: null);
        context.Request.Host = new HostString("acme.inventory.app");
        var registry = RegistryAcmeFor(MemberId);

        await InvokeAsync(context, state, registry);

        Assert.True(state.IsResolved);
        Assert.Equal("acme", state.Slug);
    }

    [Fact]
    public async Task QueryHint_Resolves()
    {
        var (context, state) = Build("/inventory?tenant=acme", member: MemberId, header: null);
        var registry = RegistryAcmeFor(MemberId);

        await InvokeAsync(context, state, registry);

        Assert.True(state.IsResolved);
        Assert.Equal("acme", state.Slug);
    }

    [Fact]
    public async Task CookieHint_Resolves_AsLowestPrecedenceFallback()
    {
        var (context, state) = Build("/inventory", member: MemberId, header: null);
        context.Request.Headers.Cookie = $"{TenantResolutionConstants.CookieName}=acme";
        var registry = RegistryAcmeFor(MemberId);

        await InvokeAsync(context, state, registry);

        Assert.True(state.IsResolved);
        Assert.Equal("acme", state.Slug);
    }

    [Fact]
    public async Task CookieHint_ResolvesOnBlazorHandshake_AndNeverFailsClosedOnAgnosticPath()
    {
        // Mirrors the real SignalR handshake: agnostic path, no header, continuity cookie only.
        var (context, state) = Build("/_blazor", member: MemberId, header: null);
        context.Request.Headers.Cookie = $"{TenantResolutionConstants.CookieName}=acme";
        var registry = RegistryAcmeFor(MemberId);

        var called = await InvokeAsync(context, state, registry);

        Assert.True(called);
        Assert.True(state.IsResolved);
        Assert.Equal(AcmeId, state.TenantId);
    }

    [Fact]
    public async Task Slug_IsNormalisedToLowerCaseBeforeLookup()
    {
        var (context, state) = Build("/inventory", member: MemberId, header: "ACME");
        var registry = RegistryAcmeFor(MemberId);

        await InvokeAsync(context, state, registry);

        Assert.True(state.IsResolved);
        Assert.Equal("acme", state.Slug);
    }

    [Fact]
    public async Task NonMember_OnAgnosticPath_WithHint_ContinuesUnresolved()
    {
        // REQ-008: a tenant-agnostic path never fails because of a hint. A foreign hint is
        // simply ignored (no 403), rather than the old behaviour that 403'd here.
        var (context, state) = Build("/", member: OutsiderId, header: "acme");
        var registry = RegistryAcmeFor(MemberId);

        var called = await InvokeAsync(context, state, registry);

        Assert.True(called);
        Assert.Equal(200, context.Response.StatusCode);
        Assert.False(state.IsResolved);
    }

    // ------------------------------------------- §6 anti-enumeration (byte-identical)

    [Fact]
    public async Task EveryDeniedCause_ProducesAByteIdentical403()
    {
        // Unknown tenant.
        var (unknownCtx, unknownState) = Build("/inventory", MemberId, "does-not-exist");
        await InvokeAsync(unknownCtx, unknownState, RegistryAcmeFor(MemberId));

        // Suspended tenant: the registry yields null for anything that is not Active, exactly as the
        // real TenantRegistry does for a non-Active row — so the middleware cannot distinguish it.
        var suspendedRegistry = new FakeTenantRegistry();
        suspendedRegistry.ForUser[MemberId] = [Acme];
        var (suspendedCtx, suspendedState) = Build("/inventory", MemberId, "acme");
        await InvokeAsync(suspendedCtx, suspendedState, suspendedRegistry);

        // Non-member.
        var (nonMemberCtx, nonMemberState) = Build("/inventory", OutsiderId, "acme");
        await InvokeAsync(nonMemberCtx, nonMemberState, RegistryAcmeFor(MemberId));

        // Unparseable slug.
        var (badSlugCtx, badSlugState) = Build("/inventory", MemberId, "ACME!!");
        await InvokeAsync(badSlugCtx, badSlugState, RegistryAcmeFor(MemberId));

        // Unusable identity: authenticated but NameIdentifier is not a Guid.
        var (badIdentityCtx, badIdentityState) = BuildAuthenticated("/inventory", nameIdentifier: "not-a-guid", header: "acme");
        await InvokeAsync(badIdentityCtx, badIdentityState, RegistryAcmeFor(MemberId));

        var snapshots = new[]
        {
            Capture(unknownCtx),
            Capture(suspendedCtx),
            Capture(nonMemberCtx),
            Capture(badSlugCtx),
            Capture(badIdentityCtx)
        };

        foreach (var snapshot in snapshots)
        {
            Assert.Equal(403, snapshot.StatusCode);
            Assert.Equal(TenantResolutionConstants.AccessDeniedContentType, snapshot.ContentType);
            Assert.Equal(TenantResolutionConstants.AccessDeniedMessage, snapshot.BodyText);

            // The body must never echo the supplied slug.
            Assert.DoesNotContain("acme", snapshot.BodyText, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("does-not-exist", snapshot.BodyText, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Unauthenticated_EveryHintOutcome_IsByteIdentical_AndPerformsNoLookup()
    {
        // (a) no hint, (b) valid slug, (c) unknown slug, (d) suspended slug: unauthenticated, all
        // collapse to the same challenge and NO tenant lookup is even attempted. /api/* so the
        // middleware writes its own bare 401 rather than the Identity redirect.
        var cases = new (string? Header, string Label)[]
        {
            (null, "no hint"),
            ("acme", "valid slug"),
            ("does-not-exist", "unknown slug"),
            ("acme", "suspended slug")
        };

        var snapshots = new List<(string Label, CapturedResponse Response)>();

        foreach (var (header, label) in cases)
        {
            // Even a slug that would resolve for a member is never touched, because auth comes first.
            var registry = RegistryAcmeFor(MemberId);
            var (context, state) = Build("/api/products", member: null, header: header);

            var called = await InvokeAsync(context, state, registry);

            Assert.False(called);
            Assert.Equal(0, registry.BySlugCalls);
            Assert.Equal(0, registry.ForUserCalls);
            Assert.False(state.IsResolved);
            snapshots.Add((label, Capture(context)));
        }

        var baseline = snapshots[0].Response;

        foreach (var (label, snapshot) in snapshots)
        {
            Assert.Equal(401, snapshot.StatusCode);
            Assert.Equal(baseline.ContentType, snapshot.ContentType);
            Assert.Equal(baseline.Body, snapshot.Body);          // full bytes, not just status
            Assert.Equal(baseline.SetCookie, snapshot.SetCookie);
        }
    }

    [Fact]
    public async Task Unauthenticated_Request_PerformsNoTenantLookup()
    {
        // Auth first means the registry is never touched, so no tenant existence can be probed.
        var registry = new FakeTenantRegistry();
        var (context, state) = Build("/api/products", member: null, header: "acme");

        var called = await InvokeAsync(context, state, registry);

        Assert.False(called);
        Assert.Equal(401, context.Response.StatusCode);
        Assert.Equal(0, registry.BySlugCalls);
        Assert.Equal(0, registry.ForUserCalls);
    }

    [Fact]
    public async Task Authenticated_WithHint_AlwaysPerformsBothLookups_EvenOnFailure()
    {
        // Equalised work: a non-member must run the membership lookup AND the tenant lookup, exactly
        // like an unknown tenant, so timing cannot discriminate between the two causes.
        var nonMemberRegistry = RegistryAcmeFor(MemberId);
        var (nonMemberCtx, nonMemberState) = Build("/inventory", OutsiderId, "acme");
        await InvokeAsync(nonMemberCtx, nonMemberState, nonMemberRegistry);

        var unknownRegistry = RegistryAcmeFor(MemberId);
        var (unknownCtx, unknownState) = Build("/inventory", MemberId, "does-not-exist");
        await InvokeAsync(unknownCtx, unknownState, unknownRegistry);

        Assert.Equal(1, nonMemberRegistry.BySlugCalls);
        Assert.Equal(1, nonMemberRegistry.ForUserCalls);
        Assert.Equal(1, unknownRegistry.BySlugCalls);
        Assert.Equal(1, unknownRegistry.ForUserCalls);
    }

    // -------------------------------------------------- §6 source-aware cookie handling

    [Fact]
    public async Task CookieSourcedFailure_ClearsTheContinuityCookie()
    {
        var (context, state) = Build("/inventory", MemberId, header: null);
        context.Request.Headers.Cookie = $"{TenantResolutionConstants.CookieName}=does-not-exist";
        var registry = RegistryAcmeFor(MemberId);

        var called = await InvokeAsync(context, state, registry);

        Assert.False(called);
        Assert.Equal(403, context.Response.StatusCode);
        Assert.Contains(TenantResolutionConstants.CookieName, SetCookieHeader(context));
    }

    [Fact]
    public async Task CookieSourcedUnparseableSlug_ClearsTheContinuityCookie()
    {
        // The old code forgot to clear the cookie on the unparseable-slug branch; it must clear now.
        var (context, state) = Build("/inventory", MemberId, header: null);
        context.Request.Headers.Cookie = $"{TenantResolutionConstants.CookieName}=ACME!!";
        var registry = RegistryAcmeFor(MemberId);

        var called = await InvokeAsync(context, state, registry);

        Assert.False(called);
        Assert.Equal(403, context.Response.StatusCode);
        Assert.Contains(TenantResolutionConstants.CookieName, SetCookieHeader(context));
    }

    [Fact]
    public async Task HeaderSourcedFailure_DoesNotTouchTheContinuityCookie()
    {
        // A valid selection lives in the cookie; a header-sourced failure must leave it alone.
        var (context, state) = Build("/inventory", MemberId, header: "does-not-exist");
        context.Request.Headers.Cookie = $"{TenantResolutionConstants.CookieName}=acme";
        var registry = RegistryAcmeFor(MemberId);

        var called = await InvokeAsync(context, state, registry);

        Assert.False(called);
        Assert.Equal(403, context.Response.StatusCode);
        Assert.DoesNotContain(TenantResolutionConstants.CookieName, SetCookieHeader(context));
    }

    [Fact]
    public async Task QuerySourcedFailure_DoesNotTouchTheContinuityCookie()
    {
        var (context, state) = Build("/?tenant=does-not-exist", MemberId, header: null);
        context.Request.Headers.Cookie = $"{TenantResolutionConstants.CookieName}=acme";
        var registry = RegistryAcmeFor(MemberId);

        var called = await InvokeAsync(context, state, registry);

        Assert.True(called);
        Assert.DoesNotContain(TenantResolutionConstants.CookieName, SetCookieHeader(context));
    }

    [Theory]
    [InlineData("?tenant=does-not-exist")]  // query-sourced foreign hint
    [InlineData("?tenant=acme")]            // query-sourced foreign (non-member) hint
    public async Task AgnosticPath_WithQueryHint_NeverFailsNorClearsTheCookie(string query)
    {
        // The crafted-link vector: an allowlisted path with a foreign ?tenant= must not 403 and must
        // not wipe the continuity cookie.
        var (context, state) = Build("/" + query, OutsiderId, header: null);
        context.Request.Headers.Cookie = $"{TenantResolutionConstants.CookieName}=acme";
        var registry = RegistryAcmeFor(MemberId);

        var called = await InvokeAsync(context, state, registry);

        Assert.True(called);
        Assert.NotEqual(403, context.Response.StatusCode);
        Assert.DoesNotContain(TenantResolutionConstants.CookieName, SetCookieHeader(context));
    }

    [Fact]
    public async Task AgnosticPath_WithCookieSourcedForeignHint_DoesNotClearTheCookie()
    {
        var (context, state) = Build("/", MemberId, header: null);
        context.Request.Headers.Cookie = $"{TenantResolutionConstants.CookieName}=foreign";
        var registry = RegistryAcmeFor(MemberId);

        var called = await InvokeAsync(context, state, registry);

        Assert.True(called);
        Assert.NotEqual(403, context.Response.StatusCode);
        Assert.DoesNotContain(TenantResolutionConstants.CookieName, SetCookieHeader(context));
    }

    [Fact]
    public async Task SuccessfulResolution_PublishesTheContinuityCookie()
    {
        var (context, state) = Build("/inventory", MemberId, "acme");
        var registry = RegistryAcmeFor(MemberId);

        await InvokeAsync(context, state, registry);

        Assert.Contains(TenantResolutionConstants.CookieName, SetCookieHeader(context));
    }

    // --------------------------------------------------------------- circuit bridge

    [Fact]
    public async Task CircuitHandler_SeedsState_FromHttpContextItems()
    {
        var context = new DefaultHttpContext();
        context.Items[TenantResolutionConstants.HttpContextItemKey] = Acme;

        var state = new TenantState();
        var handler = new TenantCircuitHandler(state, new HttpContextAccessor { HttpContext = context });

        await handler.OnCircuitOpenedAsync(null!, CancellationToken.None);

        Assert.True(state.IsResolved);
        Assert.Equal(AcmeId, state.TenantId);
        Assert.Equal("acme", state.Slug);
    }

    [Fact]
    public async Task CircuitHandler_WithNoSeed_LeavesCircuitUnresolved()
    {
        var context = new DefaultHttpContext();
        var state = new TenantState();
        var handler = new TenantCircuitHandler(state, new HttpContextAccessor { HttpContext = context });

        await handler.OnCircuitOpenedAsync(null!, CancellationToken.None);

        Assert.False(state.IsResolved);
    }

    [Fact]
    public async Task CircuitHandler_WithNoHttpContext_LeavesCircuitUnresolved()
    {
        var state = new TenantState();
        var handler = new TenantCircuitHandler(state, new HttpContextAccessor { HttpContext = null });

        await handler.OnCircuitOpenedAsync(null!, CancellationToken.None);

        Assert.False(state.IsResolved);
    }

    // ------------------------------------------------------- mid-circuit switch seam

    [Fact]
    public void TenantState_Set_ChangesContextAndRaisesChanged()
    {
        ITenantContext context = new TenantState();
        var raised = 0;
        ((TenantState)context).Changed += () => raised++;

        ((TenantState)context).Set(AcmeId, "acme");

        Assert.True(context.IsResolved);
        Assert.Equal(AcmeId, context.TenantId);
        Assert.Equal("acme", context.Slug);

        // Switcher semantics: a second Set mid-circuit retargets without a new HTTP request.
        ((TenantState)context).Set(GlobexId, "globex");

        Assert.Equal(GlobexId, context.TenantId);
        Assert.Equal("globex", context.Slug);
        Assert.Equal(2, raised);

        ((TenantState)context).Clear();

        Assert.False(context.IsResolved);
        Assert.Equal(3, raised);
    }

    // ----------------------------------------------------------------- path allowlist

    [Theory]
    [InlineData("/")]
    [InlineData("/counter")]
    [InlineData("/weather")]
    [InlineData("/Account/Login")]
    [InlineData("/_blazor")]
    [InlineData("/_framework/blazor.web.js")]
    [InlineData("/not-found")]
    public void TenantAgnosticPaths_Classifies_KnownAgnosticPaths(string path)
    {
        Assert.True(TenantAgnosticPaths.IsTenantAgnostic(new PathString(path), new TestWebHostEnvironment()));
    }

    [Theory]
    [InlineData("/inventory")]
    [InlineData("/api/products")]
    [InlineData("/tenant-required")]
    [InlineData("/health")]                       // REQ-008: removed from the allowlist, no endpoint exists
    [InlineData("/healthz")]
    [InlineData("/.well-known/openid-configuration")]  // REQ-008: removed from the allowlist
    public void TenantAgnosticPaths_Classifies_TenantScopedPaths(string path)
    {
        Assert.False(TenantAgnosticPaths.IsTenantAgnostic(new PathString(path), new TestWebHostEnvironment()));
    }

    [Fact]
    public void Directory_UnderWwwRoot_IsNotTenantAgnostic()
    {
        var root = Path.Combine(Path.GetTempPath(), "acentra-agnostic-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "assets"));
        File.WriteAllText(Path.Combine(root, "assets", "site.css"), "body{}");

        try
        {
            var environment = new TestWebHostEnvironment
            {
                WebRootPath = root,
                WebRootFileProvider = new PhysicalFileProvider(root)
            };

            // A directory is not a file: a whole tree must not become tenant-agnostic.
            Assert.False(TenantAgnosticPaths.IsTenantAgnostic(new PathString("/assets"), environment));

            // A real file inside it still is tenant-agnostic.
            Assert.True(TenantAgnosticPaths.IsTenantAgnostic(new PathString("/assets/site.css"), environment));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ------------------------------------------------------- Web DI seam wiring

    [Fact]
    public void AddTenantResolution_WiresResolversCircuitHandlerAndAccessor()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        // Mirrors the two mandated registrations in Program.cs.
        services.AddScoped<TenantState>();
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantState>());

        services.AddTenantResolution();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>());

        var resolvers = scope.ServiceProvider.GetServices<ITenantResolver>().ToList();
        Assert.Collection(
            resolvers,
            r => Assert.IsType<HeaderTenantResolver>(r),
            r => Assert.IsType<QueryTenantResolver>(r),
            r => Assert.IsType<SubdomainTenantResolver>(r),
            r => Assert.IsType<CookieTenantResolver>(r));

        var handlers = scope.ServiceProvider.GetServices<CircuitHandler>().ToList();
        Assert.Contains(handlers, h => h is TenantCircuitHandler);
    }

    // -------------------------------------------------------------------- helpers

    private static FakeTenantRegistry RegistryAcmeFor(Guid memberId)
    {
        var registry = new FakeTenantRegistry();
        registry.BySlug["acme"] = Acme;
        registry.ForUser[memberId] = [Acme];
        return registry;
    }

    private static (DefaultHttpContext Context, TenantState State) Build(string path, Guid? member, string? header)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = new PathString(path.Split('?')[0]);
        context.Request.QueryString = new QueryString(path.Contains('?') ? "?" + path.Split('?', 2)[1] : string.Empty);
        context.Request.Host = new HostString("localhost");
        context.Response.Body = new MemoryStream();

        if (header is not null)
        {
            context.Request.Headers[TenantResolutionConstants.HeaderName] = header;
        }

        if (member is { } id)
        {
            context.User = AuthenticatedUser(id.ToString());
        }

        return (context, new TenantState());
    }

    private static (DefaultHttpContext Context, TenantState State) BuildAuthenticated(
        string path, string nameIdentifier, string? header)
    {
        var (context, state) = Build(path, member: null, header);
        context.User = AuthenticatedUser(nameIdentifier);
        return (context, state);
    }

    private static ClaimsPrincipal AuthenticatedUser(string nameIdentifier) =>
        new(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, nameIdentifier)],
            authenticationType: "test"));

    private static async Task<bool> InvokeAsync(HttpContext context, TenantState state, ITenantRegistry registry)
    {
        var called = false;

        var middleware = new TenantResolutionMiddleware(_ =>
        {
            called = true;
            return Task.CompletedTask;
        });

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
            new TestWebHostEnvironment(),
            NullLogger<TenantResolutionMiddleware>.Instance);

        return called;
    }

    private static string BodyText(HttpContext context) => Capture(context).BodyText;

    private static string SetCookieHeader(HttpContext context) =>
        context.Response.Headers.SetCookie.ToString();

    private static CapturedResponse Capture(HttpContext context)
    {
        var body = ((MemoryStream)context.Response.Body).ToArray();

        return new CapturedResponse(
            context.Response.StatusCode,
            context.Response.ContentType,
            body,
            Encoding.UTF8.GetString(body),
            context.Response.Headers.SetCookie.ToString());
    }

    private sealed record CapturedResponse(
        int StatusCode,
        string? ContentType,
        byte[] Body,
        string BodyText,
        string SetCookie);

    private sealed class FakeTenantRegistry : ITenantRegistry
    {
        public Dictionary<string, TenantDescriptor> BySlug { get; } = new(StringComparer.Ordinal);

        public Dictionary<Guid, List<TenantDescriptor>> ForUser { get; } = [];

        public int BySlugCalls { get; private set; }

        public int ForUserCalls { get; private set; }

        public Task<TenantDescriptor?> FindBySlugAsync(string slug, CancellationToken ct)
        {
            BySlugCalls++;
            return Task.FromResult(BySlug.GetValueOrDefault(slug));
        }

        public Task<IReadOnlyList<TenantDescriptor>> FindForUserAsync(Guid userId, CancellationToken ct)
        {
            ForUserCalls++;
            return Task.FromResult<IReadOnlyList<TenantDescriptor>>(
                ForUser.TryGetValue(userId, out var tenants) ? tenants : []);
        }
    }

    private sealed class TestWebHostEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "Acentra.Web";

        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();

        public string WebRootPath { get; set; } = string.Empty;

        public string EnvironmentName { get; set; } = "Development";

        public string ContentRootPath { get; set; } = string.Empty;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
