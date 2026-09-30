using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace Acentra.Web.Middleware;

/// <summary>
/// The tenant-agnostic allowlist. Enforcement is fail-closed by default: every request whose
/// path is NOT listed here is treated as tenant-scoped and must carry a resolvable, authorized
/// tenant — so a newly added page is protected without anyone remembering to opt in.
///
/// The list exists only to keep genuinely tenant-independent traffic working: the Identity
/// account pages, error/not-found re-execution, the Blazor framework and static assets, and
/// the REQ-001 template pages. Published publicly so the isolation matrix (REQ-006) can
/// enumerate it rather than hardcode strings.
/// </summary>
public static class TenantAgnosticPaths
{
    /// <summary>Path prefixes that never require a tenant.</summary>
    public static readonly IReadOnlyList<string> Prefixes =
    [
        "/_blazor",
        "/_framework",
        "/_content",
        "/lib",
        "/Account",
        "/Error",
        "/not-found",
        "/health",
        "/.well-known"
    ];

    /// <summary>
    /// Exact paths that never require a tenant: the scaffolded template pages plus
    /// conventional browser/probe assets that may legitimately not exist yet. These are
    /// named explicitly rather than inferred from a file extension, so enforcement is
    /// still failed closed for anything that is not a known tenant-independent endpoint.
    /// </summary>
    public static readonly IReadOnlyList<string> ExactPaths =
    [
        "/",
        "/counter",
        "/weather",
        "/favicon.ico",
        "/robots.txt",
        "/sitemap.xml",
        "/manifest.webmanifest"
    ];

    public static bool IsTenantAgnostic(PathString path, IWebHostEnvironment environment)
    {
        var value = path.HasValue ? path.Value! : "/";

        if (!value.StartsWith('/'))
        {
            value = "/" + value;
        }

        foreach (var prefix in Prefixes)
        {
            if (MatchesPrefix(value, prefix))
            {
                return true;
            }
        }

        foreach (var exact in ExactPaths)
        {
            if (string.Equals(value, exact, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // A request that maps to a real static file (favicon, css, js, fonts, wasm, …) is not
        // tenant-scoped. Checked through the file provider rather than a extension allowlist so
        // nothing can slip through on an unanticipated extension.
        var file = environment.WebRootFileProvider?.GetFileInfo(value);
        return file is { Exists: true };
    }

    private static bool MatchesPrefix(string path, string prefix) =>
        path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
        (path.Length == prefix.Length || path[prefix.Length] == '/');
}
