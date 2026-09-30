using System.Security.Claims;
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
/// Focused proof of the REQ-002 §6 resolution matrix and the Blazor circuit bridge.
/// These drive <see cref="TenantResolutionMiddleware"/> and <see cref="TenantCircuitHandler"/>
/// directly with a real <see cref="TenantState"/>/<see cref="ITenantContext"/> and the real
/// hint resolvers, so the four mandated cases are asserted deterministically (no DB, no
/// network, no SignalR flakiness).
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
    public async Task UnknownSlug_IsBadRequest()
    {
        var (context, state) = Build("/inventory", member: MemberId, header: "does-not-exist");
        var registry = RegistryAcmeFor(MemberId);

        var called = await InvokeAsync(context, state, registry);

        Assert.False(called);
        Assert.Equal(400, context.Response.StatusCode);
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
    public async Task NonMember_IsForbidden_EvenOnAgnosticPath_WhenHintIsPresent()
    {
        var (context, state) = Build("/", member: OutsiderId, header: "acme");
        var registry = RegistryAcmeFor(MemberId);

        var called = await InvokeAsync(context, state, registry);

        Assert.False(called);
        Assert.Equal(403, context.Response.StatusCode);
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
    public void TenantAgnosticPaths_Classifies_TenantScopedPaths(string path)
    {
        Assert.False(TenantAgnosticPaths.IsTenantAgnostic(new PathString(path), new TestWebHostEnvironment()));
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

        if (header is not null)
        {
            context.Request.Headers[TenantResolutionConstants.HeaderName] = header;
        }

        if (member is { } id)
        {
            context.User = new ClaimsPrincipal(
                new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, id.ToString())],
                    authenticationType: "test"));
        }

        return (context, new TenantState());
    }

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

    private sealed class FakeTenantRegistry : ITenantRegistry
    {
        public Dictionary<string, TenantDescriptor> BySlug { get; } = new(StringComparer.Ordinal);

        public Dictionary<Guid, List<TenantDescriptor>> ForUser { get; } = [];

        public Task<TenantDescriptor?> FindBySlugAsync(string slug, CancellationToken ct) =>
            Task.FromResult(BySlug.GetValueOrDefault(slug));

        public Task<IReadOnlyList<TenantDescriptor>> FindForUserAsync(Guid userId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<TenantDescriptor>>(
                ForUser.TryGetValue(userId, out var tenants) ? tenants : []);
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
