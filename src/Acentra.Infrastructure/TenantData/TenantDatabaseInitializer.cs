using Acentra.Domain.Abstractions;
using Acentra.Infrastructure.ControlPlane;
using Acentra.Infrastructure.ControlPlane.Configurations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Acentra.Infrastructure.TenantData;

/// <summary>
/// Startup work for the tenant data plane: ensures every registered tenant has its own database,
/// migrated to the current tenant schema. Development only — mirroring
/// <c>ControlPlaneInitializer</c>, so no production environment ever migrates implicitly; operators
/// run <c>dotnet ef database update</c> (or call <see cref="ITenantProvisioner"/>) explicitly.
///
/// <para>
/// The work is idempotent, so it is safe on every start, and a tenant that cannot be provisioned is
/// logged and skipped rather than taking the host down: a missing tenant database then fails loudly
/// at query time, which is where the routing decision belongs. Tenants with an empty
/// <c>DatabaseName</c> are skipped with an error for the same reason — guessing a database name
/// could point a tenant at another tenant's database.
/// </para>
/// </summary>
public sealed class TenantDatabaseInitializer(
    IServiceScopeFactory scopeFactory,
    IHostEnvironment environment,
    ILogger<TenantDatabaseInitializer> logger) : IHostedService
{
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly IHostEnvironment _environment = environment;
    private readonly ILogger<TenantDatabaseInitializer> _logger = logger;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_environment.IsDevelopment())
        {
            _logger.LogInformation(
                "Tenant database provisioning skipped (environment '{Environment}' is not Development).",
                _environment.EnvironmentName);

            return;
        }

        IReadOnlyList<TenantDescriptor> tenants;

        // A hosted service is a singleton, so nothing scoped (the control-plane context, the
        // provisioner, the per-unit-of-work context factory) may be constructor-injected here.
        await using var scope = _scopeFactory.CreateAsyncScope();

        try
        {
            tenants = await ReadTenantsAsync(scope.ServiceProvider, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The control plane is not usable yet (usually because its own initializer has not run,
            // or Postgres is unreachable). Nothing is guessed: startup continues and the failure
            // surfaces when tenant data is first requested.
            _logger.LogError(
                ex,
                "Could not read registered tenants, so no tenant database was provisioned at startup.");

            return;
        }

        if (tenants.Count == 0)
        {
            // Legitimate for a host with no tenants registered yet — not an error.
            _logger.LogInformation("No registered tenants: nothing to provision.");
            return;
        }

        var provisioner = scope.ServiceProvider.GetRequiredService<ITenantProvisioner>();

        foreach (var tenant in tenants)
        {
            if (string.IsNullOrWhiteSpace(tenant.DatabaseName))
            {
                _logger.LogError(
                    "Tenant '{Slug}' has an empty DatabaseName and was SKIPPED; its data cannot be " +
                    "provisioned until the control plane sets one.",
                    tenant.Slug);

                continue;
            }

            try
            {
                await provisioner.ProvisionAsync(tenant, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Tenant '{Slug}' could not be provisioned; its database '{Database}' stays missing " +
                    "and any request for it will fail rather than fall back to another tenant.",
                    tenant.Slug,
                    tenant.DatabaseName);
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static async Task<IReadOnlyList<TenantDescriptor>> ReadTenantsAsync(
        IServiceProvider services,
        CancellationToken ct)
    {
        var controlPlane = services.GetRequiredService<ControlPlaneDbContext>();

        return await controlPlane.Tenants
            .AsNoTracking()
            .Where(t => t.Status == TenantStatus.Active)
            .OrderBy(t => t.Slug)
            .Select(t => new TenantDescriptor(t.Id, t.Slug, t.Name, t.DatabaseName))
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }
}
