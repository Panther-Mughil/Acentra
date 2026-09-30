using Acentra.Infrastructure.ControlPlane;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Acentra.Web.Auth;

/// <summary>
/// Development-only startup work for the control plane: applies pending migrations and seeds
/// the demo tenants/user. Gated on <see cref="IHostEnvironment.IsDevelopment"/> so no
/// production environment ever migrates or seeds implicitly (operators run
/// <c>dotnet ef database update</c>). Lives in the Web project so it can use the hosting
/// abstractions; the seeding logic itself lives in the Infrastructure control plane.
/// </summary>
public sealed class ControlPlaneInitializer(
    IServiceScopeFactory scopeFactory,
    IHostEnvironment environment,
    ILogger<ControlPlaneInitializer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!environment.IsDevelopment())
        {
            logger.LogInformation(
                "Control-plane startup work skipped (environment '{Environment}' is not Development).",
                environment.EnvironmentName);
            return;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var services = scope.ServiceProvider;

        var db = services.GetRequiredService<ControlPlaneDbContext>();
        var users = services.GetRequiredService<UserManager<AppUser>>();

        await db.Database.MigrateAsync(cancellationToken);
        await TenantSeeder.SeedAsync(db, users, cancellationToken);

        logger.LogInformation("Control-plane database migrated and dev seed ensured.");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
