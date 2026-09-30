using Acentra.Domain.Abstractions;
using Acentra.Infrastructure.ControlPlane;
using Acentra.Infrastructure.TenantData;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Acentra.IntegrationTests;

/// <summary>
/// REQ-008 §4.7 — pins the slug → database routing contract.
///
/// <see cref="TenantDatabaseName.FromSlug"/> is THE rule that maps a request's tenant (which carries
/// only a slug) to a database, while provisioning uses <see cref="TenantDatabaseName.Normalize"/> on
/// the authoritative <c>Tenant.DatabaseName</c> column. The query filter contains a divergence by
/// returning zero rows, but it fails <em>silently</em>; this test makes any divergence fail loudly at
/// test time instead. It iterates every real tenant row, so a newly registered tenant whose stored
/// name does not follow the convention breaks the suite.
/// </summary>
public sealed class TenantRoutingConventionTests : IClassFixture<TenantTestFactory>
{
    private readonly TenantTestFactory _factory;

    public TenantRoutingConventionTests(TenantTestFactory factory) => _factory = factory;

    [Fact]
    public async Task EveryTenantRow_FollowsTheSlugToDatabaseConvention()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();

        var tenants = await db.Tenants
            .AsNoTracking()
            .OrderBy(t => t.Slug)
            .ToListAsync();

        // The suite is only meaningful against real rows, including the seeded ones.
        Assert.NotEmpty(tenants);
        Assert.Contains(tenants, t => t.Slug == TenantSeeder.AcmeSlug);
        Assert.Contains(tenants, t => t.Slug == TenantSeeder.GlobexSlug);

        foreach (var tenant in tenants)
        {
            // The request path (slug only) must land on the authoritative stored name.
            Assert.Equal(tenant.DatabaseName, TenantDatabaseName.FromSlug(tenant.Slug));

            // The provisioning path (descriptor name) must agree with the request path.
            var descriptor = new TenantDescriptor(tenant.Id, tenant.Slug, tenant.Name, tenant.DatabaseName);
            Assert.Equal(
                TenantDatabaseName.FromSlug(descriptor.Slug),
                TenantDatabaseName.Normalize(descriptor.DatabaseName));
        }
    }
}
