using System.Net;
using System.Text;
using Acentra.Domain.Abstractions;
using Acentra.Domain.Entities;
using Acentra.Infrastructure.ControlPlane;
using Acentra.Infrastructure.Inventory;
using Acentra.Infrastructure.Storage;
using Acentra.Infrastructure.TenantData;
using Acentra.Web.Middleware;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Acentra.IntegrationTests;

/// <summary>
/// The REQ-005 §6 UI proofs, driven over HTTP against the real app (DI, tenant middleware, routing,
/// authorization, prerendering) and the real dev tenants/databases:
///
/// <list type="number">
///   <item>the products page renders only the resolved tenant's rows;</item>
///   <item>switching the tenant hint replaces the rows, and no row of the previous tenant remains in
///         the response;</item>
///   <item>both tenants may own the same SKU, each rendering its own row;</item>
///   <item>a file uploaded for one tenant is a not-found for another and cannot be downloaded.</item>
/// </list>
///
/// Storage is swapped for the local provider over a temp root so the download proof needs no network.
/// Every created row is removed in a <c>finally</c>, so the dev tenants are left as they were and the
/// suite is re-runnable.
/// </summary>
public sealed class InventoryPagesEndToEndTests : IClassFixture<InventoryPagesEndToEndTests.Factory>
{
    private readonly Factory _factory;

    public InventoryPagesEndToEndTests(Factory factory) => _factory = factory;

    // ------------------------------------------------ 1. only this tenant's rows render

    [Fact]
    public async Task ProductsPage_ShowsOnlyTheResolvedTenantsRows_AndASwitchReplacesThem()
    {
        var userId = await GetDemoUserIdAsync();
        var acme = await FindTenantAsync(TenantSeeder.AcmeSlug);
        var globex = await FindTenantAsync(TenantSeeder.GlobexSlug);

        var marker = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var sharedSku = $"SKU-SHARED-{marker}";
        var acmeOnlySku = $"SKU-ACME-{marker}";
        var globexOnlySku = $"SKU-GX-{marker}";
        var acmeSharedName = $"Acme-shared-{marker}";
        var globexSharedName = $"Globex-shared-{marker}";

        var acmeService = ServiceFor(acme);
        var globexService = ServiceFor(globex);
        var created = new List<(TenantDescriptor Tenant, Guid Id)>();

        try
        {
            // The same SKU in both tenants is legal; the names are what distinguish the rows.
            created.Add((acme, (await acmeService.CreateProductAsync(
                new ProductInput(sharedSku, acmeSharedName, null, 10m, 0, true))).Id));
            created.Add((acme, (await acmeService.CreateProductAsync(
                new ProductInput(acmeOnlySku, $"Acme-only-{marker}", null, 11m, 0, true))).Id));
            created.Add((globex, (await globexService.CreateProductAsync(
                new ProductInput(sharedSku, globexSharedName, null, 20m, 0, true))).Id));
            created.Add((globex, (await globexService.CreateProductAsync(
                new ProductInput(globexOnlySku, $"Globex-only-{marker}", null, 21m, 0, true))).Id));

            using var client = _factory.CreateClient();

            var acmeHtml = await GetHtmlAsync(client, $"/inventory/products?tenant={acme.Slug}", userId);
            Assert.Contains(sharedSku, acmeHtml, StringComparison.Ordinal);
            Assert.Contains(acmeSharedName, acmeHtml, StringComparison.Ordinal);
            Assert.Contains(acmeOnlySku, acmeHtml, StringComparison.Ordinal);
            Assert.DoesNotContain(globexSharedName, acmeHtml, StringComparison.Ordinal);
            Assert.DoesNotContain(globexOnlySku, acmeHtml, StringComparison.Ordinal);

            // The switch: the same page under the other tenant hint.
            var globexHtml = await GetHtmlAsync(client, $"/inventory/products?tenant={globex.Slug}", userId);
            Assert.Contains(globexSharedName, globexHtml, StringComparison.Ordinal);
            Assert.Contains(globexOnlySku, globexHtml, StringComparison.Ordinal);

            // Requirement #9: not one row of the previous tenant is left on the page.
            Assert.DoesNotContain(acmeSharedName, globexHtml, StringComparison.Ordinal);
            Assert.DoesNotContain(acmeOnlySku, globexHtml, StringComparison.Ordinal);

            // The shared SKU exists in both tenants — but each tenant renders exactly one row for it.
            Assert.Equal(1, CountOccurrences(acmeHtml, sharedSku));
            Assert.Equal(1, CountOccurrences(globexHtml, sharedSku));

            // Requirement #8 across reloads: the switch was persisted by the query hint, the
            // middleware published the continuity cookie, and a reload carrying ONLY that cookie (no
            // hint at all) still resolves the selected tenant and its rows.
            var reloadedHtml = await GetHtmlAsync(client, "/inventory/products", userId);
            Assert.Contains(globexSharedName, reloadedHtml, StringComparison.Ordinal);
            Assert.DoesNotContain(acmeSharedName, reloadedHtml, StringComparison.Ordinal);
        }
        finally
        {
            await DeleteAsync(acmeService, globexService, created);
        }
    }

    [Fact]
    public async Task ProductsPage_ForAnUnknownOrForeignTenant_FailsClosed_WithoutRows()
    {
        var userId = await GetDemoUserIdAsync();
        var acme = await FindTenantAsync(TenantSeeder.AcmeSlug);

        var marker = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var sku = $"SKU-CLOSED-{marker}";

        var acmeService = ServiceFor(acme);
        var created = new List<(TenantDescriptor Tenant, Guid Id)>();

        try
        {
            created.Add((acme, (await acmeService.CreateProductAsync(
                new ProductInput(sku, $"Acme-closed-{marker}", null, 1m, 0, true))).Id));

            using var client = _factory.CreateClient();

            // A slug this user is not a member of (a fresh tenant-less slug) and a non-member user:
            // both are the uniform 403, and the page (and its rows) never render.
            using var foreign = await SendAsync(client, $"/inventory/products?tenant=nope-{marker}", userId);
            Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);

            using var anotherUser = await SendAsync(
                client, $"/inventory/products?tenant={acme.Slug}", Guid.NewGuid());
            Assert.Equal(HttpStatusCode.Forbidden, anotherUser.StatusCode);
        }
        finally
        {
            await DeleteAsync(acmeService, acmeService, created);
        }
    }

    // --------------------------------------------- 2. a foreign tenant's file is not found

    [Fact]
    public async Task AFileUploadedForOneTenant_IsNotFoundWhenActingAsAnother()
    {
        var userId = await GetDemoUserIdAsync();
        var acme = await FindTenantAsync(TenantSeeder.AcmeSlug);
        var globex = await FindTenantAsync(TenantSeeder.GlobexSlug);

        var content = $"acentra-req005-{Guid.NewGuid():N}";
        var bytes = Encoding.UTF8.GetBytes(content);
        var key = await SeedFileAsync(acme, bytes, "note.txt");

        try
        {
            using var client = _factory.CreateClient();

            // The tenant owner can download it (the expires/token query parameters are not authority).
            using var owner = await SendAsync(client, $"/files/{key}?expires=1&token=ignored", userId, acme.Slug);
            Assert.Equal(HttpStatusCode.OK, owner.StatusCode);
            Assert.Equal(bytes, await owner.Content.ReadAsByteArrayAsync());

            // Another tenant asking for the very same key gets a not-found — and never the object.
            // (Both misses re-execute to the status page, so the status plus "no content" is the
            // assertion that matters; the body is not byte-stable across requests.)
            using var foreign = await SendAsync(client, $"/files/{key}?expires=1&token=ignored", userId, globex.Slug);
            Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);

            var foreignBody = await foreign.Content.ReadAsStringAsync();
            Assert.DoesNotContain(content, foreignBody, StringComparison.Ordinal);
            Assert.DoesNotContain("note.txt", foreignBody, StringComparison.Ordinal);

            // A key that does not exist at all is the same not-found outcome.
            using var missing = await SendAsync(
                client, $"/files/tenants/{acme.Id:N}/documents/absent.txt", userId, acme.Slug);
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

            // No tenant anywhere fails closed upstream: the URL never supplies the tenant. A fresh
            // client, so no continuity cookie from the requests above resolves one.
            using var tenantlessClient = _factory.CreateClient();
            using var tenantless = await SendAsync(tenantlessClient, $"/files/{key}", userId);
            Assert.True(
                tenantless.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Forbidden,
                $"Expected 400/403 without a tenant hint, got {(int)tenantless.StatusCode}.");
        }
        finally
        {
            await CleanupFileAsync(acme, key);
        }
    }

    // ----------------------------------------------------------------------- helpers

    private InventoryService ServiceFor(TenantDescriptor tenant) =>
        new(new TenantDbContextFactory(new ResolvedTenantContext(tenant.Id, tenant.Slug), Connections));

    private TenantConnectionStrings Connections =>
        TenantConnectionStrings.FromConfiguration(_factory.Services.GetRequiredService<IConfiguration>());

    private async Task<Guid> GetDemoUserIdAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var demo = await users.FindByEmailAsync(TenantSeeder.DemoUserEmail);

        Assert.NotNull(demo);
        return demo!.Id;
    }

    private async Task<TenantDescriptor> FindTenantAsync(string slug)
    {
        using var scope = _factory.Services.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<ITenantRegistry>();
        var tenant = await registry.FindBySlugAsync(slug, CancellationToken.None);

        Assert.NotNull(tenant);
        return tenant!;
    }

    private async Task<string> GetHtmlAsync(HttpClient client, string path, Guid userId)
    {
        using var response = await SendAsync(client, path, userId);
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"GET {path} returned {(int)response.StatusCode}. Body: {Shorten(body)}");

        // Prerendering means the page's rows are in the first response, so the HTML is the assertion.
        return body;
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        string path,
        Guid userId,
        string? tenant = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("X-Test-User", userId.ToString());

        if (tenant is not null)
        {
            request.Headers.Add(Acentra.Web.Auth.TenantResolutionConstants.HeaderName, tenant);
        }

        return await client.SendAsync(request);
    }

    private async Task<string> SeedFileAsync(TenantDescriptor tenant, byte[] bytes, string fileName)
    {
        using var scope = _factory.Services.CreateScope();
        var storage = scope.ServiceProvider.GetRequiredService<IFileStorage>();

        await using var content = new MemoryStream(bytes);
        var key = await storage.SaveAsync(tenant.Id, content, fileName, CancellationToken.None);

        await using var db = new TenantDbContextFactory(
                new ResolvedTenantContext(tenant.Id, tenant.Slug), Connections)
            .CreateForTenant(tenant);

        db.Files.Add(new TenantFile
        {
            Id = Guid.NewGuid(),
            Key = key,
            FileName = fileName,
            ContentType = "text/plain",
            SizeBytes = bytes.Length,
            UploadedUtc = DateTime.UtcNow
        });

        await db.SaveChangesAsync();
        return key;
    }

    private async Task CleanupFileAsync(TenantDescriptor tenant, string key)
    {
        using var scope = _factory.Services.CreateScope();
        var storage = scope.ServiceProvider.GetRequiredService<IFileStorage>();

        await using (var db = new TenantDbContextFactory(
                         new ResolvedTenantContext(tenant.Id, tenant.Slug), Connections)
                     .CreateForTenant(tenant))
        {
            var row = await db.Files.FirstOrDefaultAsync(file => file.Key == key);

            if (row is not null)
            {
                db.Files.Remove(row);
                await db.SaveChangesAsync();
            }
        }

        await storage.DeleteAsync(tenant.Id, key, CancellationToken.None);
    }

    private static async Task DeleteAsync(
        InventoryService acmeService,
        InventoryService globexService,
        IEnumerable<(TenantDescriptor Tenant, Guid Id)> created)
    {
        foreach (var (tenant, id) in created)
        {
            var service = tenant.Slug == TenantSeeder.AcmeSlug ? acmeService : globexService;

            try
            {
                await service.DeleteProductAsync(id);
            }
            catch (Exception)
            {
                // Best-effort cleanup: a failed delete must not mask the test's own result.
            }
        }
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = haystack.IndexOf(needle, StringComparison.Ordinal);

        while (index >= 0)
        {
            count++;
            index = haystack.IndexOf(needle, index + needle.Length, StringComparison.Ordinal);
        }

        return count;
    }

    private static string Shorten(string body) => body.Length > 1500 ? body[..1500] : body;

    /// <summary>
    /// The real app, with the header-driven test authentication scheme (as the other pipeline tests
    /// use) and the local storage provider over a temp root so the download proof is offline.
    /// </summary>
    public sealed class Factory : WebApplicationFactory<TenantResolutionMiddleware>
    {
        private readonly string _storageRoot =
            Path.Combine(Path.GetTempPath(), "acentra-req005-" + Guid.NewGuid().ToString("N"));

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            TestDatabase.ApplyPoolCap(builder);

            builder.ConfigureTestServices(services =>
            {
                TenantTestAuth.Register(services);

                services.RemoveAll<IFileStorage>();
                services.AddSingleton<IFileStorage>(new LocalFileStorage(
                    new StorageOptions { Provider = "Local", LocalRoot = _storageRoot }));
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);

            if (disposing && Directory.Exists(_storageRoot))
            {
                Directory.Delete(_storageRoot, recursive: true);
            }
        }
    }
}
