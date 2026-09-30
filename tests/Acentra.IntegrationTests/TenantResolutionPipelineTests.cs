using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Acentra.Infrastructure.ControlPlane;
using Acentra.Web.Auth;
using Acentra.Web.Middleware;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Acentra.IntegrationTests;

/// <summary>
/// Drives the four §6 resolution cases through the REAL pipeline (DI, auth, middleware order,
/// routing) via <see cref="WebApplicationFactory{TEntryPoint}"/>, against the dev control-plane
/// database. A test authentication scheme stands in for the Identity cookie so the middle of
/// the matrix can be exercised without scripting an antiforgery login round-trip.
/// </summary>
public sealed class TenantResolutionPipelineTests : IClassFixture<TenantResolutionPipelineTests.Factory>
{
    private const string TestScheme = "TestTenantAuth";

    private readonly Factory _factory;

    public TenantResolutionPipelineTests(Factory factory) => _factory = factory;

    private static async Task AssertStatusAsync(HttpStatusCode expected, HttpResponseMessage response)
    {
        if (response.StatusCode != expected)
        {
            var body = await response.Content.ReadAsStringAsync();
            var detail = body.Length > 1500 ? body[..1500] : body;
            Assert.Fail($"Expected {(int)expected} {expected} but got {(int)response.StatusCode}. Body: {detail}");
        }
    }

    [Fact]
    public async Task Member_WithHeader_ResolvesAndContinuesToRouting()
    {
        var demoId = await GetDemoUserIdAsync();
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User", demoId.ToString());
        client.DefaultRequestHeaders.Add(TenantResolutionConstants.HeaderName, TenantSeeder.AcmeSlug);

        // The middleware authorized the request and handed it to routing; the tenant-scoped
        // path does not exist yet (REQ-005), so routing answers 404 — NOT 400/403.
        var response = await client.GetAsync("/inventory");

        await AssertStatusAsync(HttpStatusCode.NotFound, response);
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
    public async Task UnknownSlug_IsBadRequest()
    {
        var demoId = await GetDemoUserIdAsync();
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User", demoId.ToString());
        client.DefaultRequestHeaders.Add(TenantResolutionConstants.HeaderName, "does-not-exist");

        var response = await client.GetAsync("/inventory");

        await AssertStatusAsync(HttpStatusCode.BadRequest, response);
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

    private async Task<Guid> GetDemoUserIdAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var demo = await users.FindByEmailAsync(TenantSeeder.DemoUserEmail);

        Assert.NotNull(demo);
        return demo!.Id;
    }

    public sealed class Factory : WebApplicationFactory<TenantResolutionMiddleware>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");

            builder.ConfigureTestServices(services =>
            {
                services.AddAuthentication(TestScheme)
                    .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestScheme, _ => { });
            });
        }
    }

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
                TestScheme);

            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), TestScheme)));
        }
    }
}
