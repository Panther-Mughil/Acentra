using Acentra.Domain.Abstractions;
using Acentra.Domain.Entities;
using Acentra.Infrastructure.ControlPlane;
using Acentra.Infrastructure.TenantData;
using Acentra.Web.Auth;
using Acentra.Web.Middleware;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Acentra.IntegrationTests;

/// <summary>
/// REQ-003 §6 proofs, driven against the real PostgreSQL server, the real schema produced by
/// <c>InitialTenant</c> and the real <see cref="TenantDbContextFactory"/>:
///
/// <list type="number">
///   <item><see cref="TenantDbContextFactory.Create"/> throws when no tenant is resolved (fail closed).</item>
///   <item>Provisioning tenant B creates a separate database and leaves tenant A's database untouched.</item>
///   <item>An insert with an empty <c>TenantId</c> is stamped from <see cref="ITenantContext"/>.</item>
///   <item>An insert and a modify carrying a foreign <c>TenantId</c> both throw.</item>
///   <item>A query on tenant A's database returns only rows carrying A's <c>TenantId</c> — including when
///   another tenant's row was written into A's database by a simulated connection-routing bug, and
///   regardless of which tenant built the cached EF model first.</item>
/// </list>
///
/// Two dedicated databases (<c>acentra_req003_a</c> / <c>acentra_req003_b</c>) are used so the dev
/// tenants are never touched; provisioning is idempotent, so the tests are re-runnable.
/// </summary>
public sealed class TenantDataIsolationTests : IClassFixture<TenantDataIsolationTests.HostFactory>
{
    private static readonly Guid TenantAId = Guid.Parse("a0000000-0000-4000-8000-00000000aaaa");
    private static readonly Guid TenantBId = Guid.Parse("b0000000-0000-4000-8000-00000000bbbb");

    private static readonly TenantDescriptor TenantA =
        new(TenantAId, "req003-a", "REQ-003 Tenant A", "acentra_req003_a");

    private static readonly TenantDescriptor TenantB =
        new(TenantBId, "req003-b", "REQ-003 Tenant B", "acentra_req003_b");

    private static readonly IConfiguration Configuration = LoadConfiguration();

    private static readonly TenantConnectionStrings Connections =
        TenantConnectionStrings.FromConfiguration(Configuration);

    private readonly HostFactory _host;

    public TenantDataIsolationTests(HostFactory host) => _host = host;

    // ------------------------------------------------- 1. fail closed without a tenant

    [Fact]
    public void Create_Throws_WhenTenantIsNotResolved()
    {
        var state = new TenantState();
        Assert.False(state.IsResolved);

        var factory = new TenantDbContextFactory(state, Connections);

        var exception = Assert.Throws<InvalidOperationException>(() => factory.Create());

        Assert.Contains("no tenant is resolved", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("fail closed", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Create_Throws_AfterTheTenantIsClearedMidCircuit()
    {
        // The switcher can clear the tenant mid-circuit; a context request must then fail, never
        // silently fall back to the previous tenant's database.
        var state = new TenantState();
        state.Set(TenantAId, TenantA.Slug);

        var factory = new TenantDbContextFactory(state, Connections);

        using (factory.Create())
        {
            // Resolution is per call: one successful Create does not make later ones legitimate.
        }

        state.Clear();

        Assert.Throws<InvalidOperationException>(() => factory.Create());
    }

    // ------------------------------- 2. tenant B is physically separate from tenant A

    [Fact]
    public async Task Provisioning_TenantB_CreatesSeparateDatabase_AndLeavesTenantAUntouched()
    {
        await ProvisionAsync(TenantA);

        var skuA = NewSku("A");
        var productAId = Guid.NewGuid();

        await using (var dbA = CreateFor(TenantA))
        {
            dbA.Products.Add(NewProduct(skuA, productAId));
            await dbA.SaveChangesAsync();
        }

        // Provisioning B must not disturb A.
        await ProvisionAsync(TenantB);

        var skuB = NewSku("B");
        await using (var dbB = CreateFor(TenantB))
        {
            dbB.Products.Add(NewProduct(skuB, Guid.NewGuid()));
            await dbB.SaveChangesAsync();
        }

        await using (var dbA = CreateFor(TenantA))
        {
            Assert.Equal("acentra_req003_a", dbA.Database.GetDbConnection().Database);
            Assert.True(await dbA.Products.AnyAsync(p => p.Id == productAId));
            Assert.False(await dbA.Products.AnyAsync(p => p.Sku == skuB));
        }

        await using (var dbB = CreateFor(TenantB))
        {
            Assert.Equal("acentra_req003_b", dbB.Database.GetDbConnection().Database);
            Assert.True(await dbB.Products.AnyAsync(p => p.Sku == skuB));
            Assert.False(await dbB.Products.AnyAsync(p => p.Id == productAId));
        }
    }

    [Fact]
    public async Task Provisioning_AnAlreadyProvisionedTenant_IsIdempotent_AndPreservesData()
    {
        await ProvisionAsync(TenantA);

        var productId = Guid.NewGuid();
        var sku = NewSku("idempotent");

        await using (var db = CreateFor(TenantA))
        {
            db.Products.Add(NewProduct(sku, productId));
            await db.SaveChangesAsync();
        }

        // REQ-003 §8: provisioning twice must be a no-op, never a reset.
        await ProvisionAsync(TenantA);
        await ProvisionAsync(TenantA);

        await using (var db = CreateFor(TenantA))
        {
            Assert.True(await db.Products.AnyAsync(p => p.Id == productId));
        }
    }

    [Fact]
    public async Task SaveChanges_Throws_WhenNoTenantIsResolved()
    {
        await ProvisionAsync(TenantA);

        // Fail closed on the write path too: an unresolved tenant has no TenantId to stamp and no
        // TenantId to verify against.
        var unresolved = new TenantState();
        Assert.False(unresolved.IsResolved);

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(Connections.ForDatabase(TenantA.DatabaseName))
            .AddInterceptors(new TenantStampInterceptor(unresolved))
            .Options;

        await using var db = new AppDbContext(options, unresolved);

        db.Products.Add(NewProduct(NewSku("unresolved"), Guid.NewGuid()));

        var exception = await Assert.ThrowsAsync<TenantIsolationException>(() => db.SaveChangesAsync());

        Assert.Contains("no tenant is resolved", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------- 3. an empty TenantId is stamped from ITenantContext

    [Fact]
    public async Task Insert_WithEmptyTenantId_IsStampedFromTenantContext()
    {
        await ProvisionAsync(TenantA);

        var state = new TenantState();
        state.Set(TenantAId, TenantA.Slug);

        var factory = new TenantDbContextFactory(state, Connections);
        var productId = Guid.NewGuid();
        var sku = NewSku("stamp");

        await using (var db = factory.Create())
        {
            // TenantId deliberately left at Guid.Empty: the client never supplies it.
            db.Products.Add(NewProduct(sku, productId, tenantId: Guid.Empty));

            await db.SaveChangesAsync();
        }

        await using (var db = factory.Create())
        {
            var stored = await db.Products.SingleAsync(p => p.Id == productId);

            Assert.Equal(TenantAId, stored.TenantId);
            Assert.NotEqual(Guid.Empty, stored.TenantId);
        }
    }

    // ------------------------------- 4. a foreign TenantId on insert/modify is refused

    [Fact]
    public async Task Insert_CarryingForeignTenantId_Throws_AndWritesNothing()
    {
        await ProvisionAsync(TenantA);

        var sku = NewSku("smuggle-insert");

        await using (var db = CreateFor(TenantA))
        {
            db.Products.Add(NewProduct(sku, Guid.NewGuid(), tenantId: TenantBId));

            var exception = await Assert.ThrowsAsync<TenantIsolationException>(() => db.SaveChangesAsync());

            Assert.Contains("Refusing to insert", exception.Message);
            Assert.Contains(TenantBId.ToString(), exception.Message);
        }

        await using (var verify = CreateFor(TenantA))
        {
            Assert.False(await verify.Products.AnyAsync(p => p.Sku == sku));
        }
    }

    [Fact]
    public async Task Modify_CarryingForeignTenantId_Throws_AndLeavesTheRowAlone()
    {
        await ProvisionAsync(TenantA);

        var productId = Guid.NewGuid();
        var sku = NewSku("smuggle-modify");

        await using (var seed = CreateFor(TenantA))
        {
            seed.Products.Add(NewProduct(sku, productId));
            await seed.SaveChangesAsync();
        }

        await using (var db = CreateFor(TenantA))
        {
            var product = await db.Products.SingleAsync(p => p.Id == productId);

            product.TenantId = TenantBId;

            var exception = await Assert.ThrowsAsync<TenantIsolationException>(() => db.SaveChangesAsync());

            Assert.Contains("Refusing to modify", exception.Message);
        }

        await using (var verify = CreateFor(TenantA))
        {
            var stored = await verify.Products.SingleAsync(p => p.Id == productId);

            Assert.Equal(TenantAId, stored.TenantId);
        }
    }

    [Fact]
    public async Task Modify_OfTheOwnTenantRow_Succeeds()
    {
        // The interceptor must not be over-eager: an ordinary update of a row that already belongs
        // to the current tenant is legitimate.
        await ProvisionAsync(TenantA);

        var productId = Guid.NewGuid();
        var sku = NewSku("own-modify");

        await using (var seed = CreateFor(TenantA))
        {
            seed.Products.Add(NewProduct(sku, productId));
            await seed.SaveChangesAsync();
        }

        await using (var db = CreateFor(TenantA))
        {
            var product = await db.Products.SingleAsync(p => p.Id == productId);

            product.Name = "Renamed by the owning tenant";
            product.UnitPrice = 99.50m;
            product.UpdatedUtc = DateTime.UtcNow;

            Assert.Equal(1, await db.SaveChangesAsync());
        }

        await using (var verify = CreateFor(TenantA))
        {
            var stored = await verify.Products.SingleAsync(p => p.Id == productId);

            Assert.Equal("Renamed by the owning tenant", stored.Name);
            Assert.Equal(99.50m, stored.UnitPrice);
        }
    }

    // ---- 5. the query filter still works, on top of the physical split and a cached model

    [Fact]
    public async Task Query_OnTenantADatabase_ReturnsOnlyRowsCarryingTenantAId()
    {
        await ProvisionAsync(TenantA);

        var skuA = NewSku("filter-a");
        var skuB = NewSku("filter-b");

        await using (var dbA = CreateFor(TenantA))
        {
            dbA.Products.Add(NewProduct(skuA, Guid.NewGuid()));
            await dbA.SaveChangesAsync();
        }

        // Simulate a connection-routing bug: tenant B's context, pointed at tenant A's database.
        // The write is legitimate for B (its own TenantId), so the interceptor allows it, and the
        // row now physically sits in A's database.
        var foreignId = Guid.NewGuid();

        await using (var mismatched = BuildContextAgainstDatabase(TenantA.DatabaseName, TenantB))
        {
            mismatched.Products.Add(NewProduct(skuB, foreignId, tenantId: TenantBId));
            await mismatched.SaveChangesAsync();
        }

        await using (var dbA = CreateFor(TenantA))
        {
            var matching = await dbA.Products
                .Where(p => p.Sku == skuA || p.Sku == skuB)
                .ToListAsync();

            // Only A's row comes back, and it is the only one carrying A's TenantId.
            Assert.Equal([skuA], matching.Select(p => p.Sku).ToArray());
            Assert.All(matching, p => Assert.Equal(TenantAId, p.TenantId));

            // The foreign row is not addressable at all, not merely filtered out of a list.
            Assert.False(await dbA.Products.AnyAsync(p => p.Id == foreignId));
        }

        // The model was already built and cached by the context above; a context for B reading the
        // SAME database must still see B's row and not A's. If the cached model had baked the first
        // tenant's id into the filter, these expectations would be inverted.
        await using (var dbB = BuildContextAgainstDatabase(TenantA.DatabaseName, TenantB))
        {
            var matching = await dbB.Products
                .Where(p => p.Sku == skuA || p.Sku == skuB)
                .ToListAsync();

            Assert.Equal([skuB], matching.Select(p => p.Sku).ToArray());
            Assert.All(matching, p => Assert.Equal(TenantBId, p.TenantId));
            Assert.False(await dbB.Products.AnyAsync(p => p.Sku == skuA));
        }
    }

    // ------------------------------------- DI composition + the startup provisioning hook

    [Fact]
    public void AddTenantData_RegistersTheFactoryProvisionerAndInitializer_ButNeverTheContext()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ITenantContext>(new TenantState());
        services.AddSingleton<IHostEnvironment>(new StubHostEnvironment());
        services.AddTenantData(Configuration);

        // AppDbContext must never be a resolvable/shared service: it is tenant-scoped and the tenant
        // can change mid-circuit.
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(AppDbContext));

        using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        using var scope = provider.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<TenantConnectionStrings>());
        Assert.IsType<TenantDbContextFactory>(scope.ServiceProvider.GetRequiredService<ITenantDbContextFactory>());
        Assert.IsType<TenantProvisioner>(scope.ServiceProvider.GetRequiredService<ITenantProvisioner>());
        Assert.Contains(provider.GetServices<IHostedService>(), service => service is TenantDatabaseInitializer);

        // …and the factory resolved from the container fails closed for an unresolved tenant.
        var factory = scope.ServiceProvider.GetRequiredService<ITenantDbContextFactory>();
        Assert.Throws<InvalidOperationException>(() => factory.Create());
    }

    [Fact]
    public void ControlPlaneModel_DoesNotIncludeTenantDataEntities()
    {
        // Regression guard. ControlPlaneDbContext discovers configurations with
        // ApplyConfigurationsFromAssembly, so an IEntityTypeConfiguration implementation inside
        // TenantData/Configurations injected the tenant tables into the *control-plane* model and made
        // its pending-model-changes validation fail at startup. The tenant configurations are
        // therefore explicit static appliers; this asserts the control-plane model stays unaffected.
        var options = new DbContextOptionsBuilder<ControlPlaneDbContext>()
            .UseNpgsql("Host=localhost;Port=5432;Database=acentra;Username=acentra")
            .Options;

        using var controlPlane = new ControlPlaneDbContext(options);

        var entityNames = controlPlane.Model.GetEntityTypes().Select(e => e.ClrType.Name).ToList();

        Assert.Contains("Tenant", entityNames);
        Assert.Contains("TenantMembership", entityNames);
        Assert.DoesNotContain("Product", entityNames);
        Assert.DoesNotContain("StockLevel", entityNames);
        Assert.DoesNotContain("StockMovement", entityNames);
        Assert.DoesNotContain("TenantFile", entityNames);
    }

    [Fact]
    public async Task HostedInitializer_ProvisionsTheSeededTenantsDatabases_AtStartup()
    {
        // The real host ran TenantDatabaseInitializer at startup (Development), which provisioned and
        // migrated every registered tenant database from the control plane's Tenant.DatabaseName.
        var descriptor = await FindSeededTenantAsync(TenantSeeder.AcmeSlug);

        Assert.False(string.IsNullOrWhiteSpace(descriptor.DatabaseName));

        using var scope = _host.Services.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<ITenantDbContextFactory>();

        await using var db = factory.CreateForTenant(descriptor);

        // Reaching the schema proves the database exists AND carries the InitialTenant migration.
        var count = await db.Products.CountAsync();

        Assert.True(count >= 0);
        Assert.Equal(TenantDatabaseName.Normalize(descriptor.DatabaseName), db.Database.GetDbConnection().Database);
    }

    // --------------------------------------------------------------------------- helpers

    private static ITenantDbContextFactory FactoryFor(TenantDescriptor tenant) =>
        new TenantDbContextFactory(new ResolvedTenantContext(tenant.Id, tenant.Slug), Connections);

    private static AppDbContext CreateFor(TenantDescriptor tenant) => FactoryFor(tenant).CreateForTenant(tenant);

    /// <summary>
    /// Builds a context on an explicit database with an explicit tenant — the only way to express
    /// "the connection and the tenant disagree", which is the failure mode the query filter exists
    /// to contain. No raw SQL is used anywhere.
    /// </summary>
    private static AppDbContext BuildContextAgainstDatabase(string databaseName, TenantDescriptor tenant)
    {
        var scope = new ResolvedTenantContext(tenant.Id, tenant.Slug);

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(Connections.ForDatabase(databaseName))
            .AddInterceptors(new TenantStampInterceptor(scope))
            .Options;

        return new AppDbContext(options, scope);
    }

    private static async Task ProvisionAsync(TenantDescriptor tenant)
    {
        var provisioner = new TenantProvisioner(
            Connections,
            new TenantDbContextFactory(new TenantState(), Connections),
            NullLogger<TenantProvisioner>.Instance);

        await provisioner.ProvisionAsync(tenant, CancellationToken.None);
    }

    private async Task<TenantDescriptor> FindSeededTenantAsync(string slug)
    {
        using var scope = _host.Services.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<ITenantRegistry>();

        var descriptor = await registry.FindBySlugAsync(slug, CancellationToken.None);

        Assert.NotNull(descriptor);
        return descriptor!;
    }

    private static Product NewProduct(string sku, Guid id, Guid tenantId = default) => new()
    {
        Id = id,
        TenantId = tenantId,
        Sku = sku,
        Name = $"Product {sku}",
        Description = "REQ-003 isolation test row",
        UnitPrice = 12.34m,
        ReorderLevel = 5,
        IsActive = true,
        CreatedUtc = DateTime.UtcNow,
        UpdatedUtc = DateTime.UtcNow
    };

    private static string NewSku(string label) => $"SKU-{label}-{Guid.NewGuid():N}"[..24];

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Acentra.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the repository root (Acentra.slnx).");
    }

    private static IConfiguration LoadConfiguration() =>
        new ConfigurationBuilder()
            .AddJsonFile(
                Path.Combine(RepositoryRoot(), "src", "Acentra.Web", "appsettings.Development.json"),
                optional: false)
            .AddJsonFile(
                Path.Combine(RepositoryRoot(), "src", "Acentra.Web", "appsettings.json"),
                optional: false)
            .AddEnvironmentVariables()
            .Build();

    /// <summary>Hosts the real app so the startup provisioning hook is exercised, not mocked.</summary>
    public sealed class HostFactory : WebApplicationFactory<TenantResolutionMiddleware>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");

            base.ConfigureWebHost(builder);
        }
    }

    private sealed class StubHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";

        public string ApplicationName { get; set; } = "Acentra.Tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
