using System.Text.Json;
using Acentra.Domain.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Acentra.Infrastructure.TenantData;

/// <summary>
/// Design-time entry point for <c>dotnet ef</c>. The runtime connection is resolved per tenant from
/// <see cref="ITenantContext"/>, so the host cannot supply one while the tooling is discovering the
/// model; this factory reads the <c>ConnectionStrings:TenantTemplate</c> template itself instead.
/// Migrations only need the model — no connection is opened for <c>migrations add</c> or
/// <c>migrations has-pending-model-changes</c> — so the database named here is never created.
///
/// <para>
/// The settings files are read directly rather than through <c>ConfigurationBuilder</c>'s JSON
/// provider: this project deliberately references only the Configuration abstractions, and adding a
/// provider package just for design-time tooling would widen the dependency graph for no runtime
/// benefit.
/// </para>
/// </summary>
public sealed class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    /// <summary>
    /// Database the design-time options point at. The maintenance database always exists, so
    /// <c>dotnet ef migrations list</c> (which reads the history table to mark migrations applied)
    /// can run. Tenant databases are never migrated through this path — the tenant connection is
    /// resolved per tenant at runtime, and <see cref="TenantProvisioner"/> is what migrates a real
    /// tenant database.
    /// </summary>
    private const string DesignTimeDatabaseName = TenantConnectionStrings.MaintenanceDatabase;

    /// <summary>Environment-variable form of <c>ConnectionStrings:TenantTemplate</c>.</summary>
    private const string TemplateEnvironmentVariable = "ConnectionStrings__TenantTemplate";

    /// <summary>
    /// Placeholder tenant for the design-time model. No query runs during model building, so the
    /// value only has to be a valid <see cref="Guid"/>.
    /// </summary>
    private static readonly ResolvedTenantContext DesignTimeTenant = new(Guid.Empty, "design-time");

    public AppDbContext CreateDbContext(string[] args)
    {
        var connections = new TenantConnectionStrings(ResolveTemplate());

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(
                connections.ForDatabase(DesignTimeDatabaseName),
                npgsql => npgsql.MigrationsAssembly(TenantDbContextFactory.MigrationsAssembly))
            .Options;

        return new AppDbContext(options, DesignTimeTenant);
    }

    /// <summary>
    /// Resolves the template, preferring the environment (always authoritative) and otherwise
    /// probing every plausible working directory, because <c>dotnet ef</c> may be invoked from the
    /// repository root, the target project or the startup project.
    /// </summary>
    private static string ResolveTemplate()
    {
        var fromEnvironment = Environment.GetEnvironmentVariable(TemplateEnvironmentVariable);

        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return fromEnvironment;
        }

        // appsettings.Development.json wins over appsettings.json, matching the host's layering.
        foreach (var fileName in new[] { "appsettings.Development.json", "appsettings.json" })
        {
            foreach (var directory in CandidateDirectories())
            {
                var file = Path.Combine(directory, fileName);

                if (!File.Exists(file))
                {
                    continue;
                }

                var template = TryReadTenantTemplate(file);

                if (!string.IsNullOrWhiteSpace(template))
                {
                    return template;
                }
            }
        }

        throw new InvalidOperationException(
            $"Could not find '{TenantConnectionStrings.ConfigurationKey}'. Run dotnet ef with " +
            "--startup-project src/Acentra.Web (so appsettings.Development.json is discoverable), or set " +
            $"the {TemplateEnvironmentVariable} environment variable.");
    }

    private static string? TryReadTenantTemplate(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var document = JsonDocument.Parse(
                stream,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip
                });

            return document.RootElement.TryGetProperty("ConnectionStrings", out var connectionStrings)
                   && connectionStrings.TryGetProperty("TenantTemplate", out var template)
                   && template.ValueKind == JsonValueKind.String
                ? template.GetString()
                : null;
        }
        catch (JsonException)
        {
            // A malformed settings file must not break design-time tooling; the next candidate may
            // still provide the template.
            return null;
        }
    }

    private static IEnumerable<string> CandidateDirectories()
    {
        var relativePaths = new[] { "src/Acentra.Web", "Acentra.Web", "." };
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var root in Roots())
        {
            foreach (var relativePath in relativePaths)
            {
                string candidate;

                try
                {
                    candidate = Path.GetFullPath(Path.Combine(root, relativePath));
                }
                catch (ArgumentException)
                {
                    continue;
                }

                if (seen.Add(candidate) && Directory.Exists(candidate))
                {
                    yield return candidate;
                }
            }
        }
    }

    private static IEnumerable<string> Roots()
    {
        string[] seeds = [Directory.GetCurrentDirectory(), AppContext.BaseDirectory];

        foreach (var seed in seeds)
        {
            var directory = new DirectoryInfo(seed);

            for (var depth = 0; directory is not null && depth < 8; depth++, directory = directory.Parent)
            {
                yield return directory.FullName;
            }
        }
    }
}
