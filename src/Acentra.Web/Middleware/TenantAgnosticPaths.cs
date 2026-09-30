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
        "/not-found"
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

    /// <summary>
    /// Static-asset extensions that are public by definition and never carry tenant data. This
    /// covers assets emitted by <c>MapStaticAssets</c>, whose fingerprinted URLs
    /// (<c>/app.4j8wku364b.css</c>) are served from a virtual route and therefore have no file on
    /// disk for the web-root probe below to find.
    /// </summary>
    public static readonly IReadOnlyList<string> StaticAssetExtensions =
    [
        ".css",
        ".js",
        ".mjs",
        ".map",
        ".json",
        ".webmanifest",
        ".wasm",
        ".png",
        ".jpg",
        ".jpeg",
        ".gif",
        ".svg",
        ".webp",
        ".ico",
        ".bmp",
        ".woff",
        ".woff2",
        ".ttf",
        ".eot",
        ".otf",
        ".txt",
        ".br",
        ".gz"
    ];

    private static readonly HashSet<string> StaticAssetExtensionSet =
        new(StaticAssetExtensions, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Application prefixes that serve tenant-uploaded content and therefore can end in any
    /// asset-looking extension. These ARE tenant data, so the static-asset extension allowance
    /// must never apply to them: they stay tenant-scoped and fail closed upstream. The only such
    /// route today is <c>/files/{**key}</c> (<c>InventoryFileEndpoints.Pattern</c>).
    /// </summary>
    private static readonly IReadOnlyList<string> TenantScopedAssetLookalikePrefixes =
    [
        "/files"
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

        // Fingerprinted assets are served from a *virtual* route by MapStaticAssets: the URL
        // exists in the endpoint manifest but there is no corresponding file in wwwroot, so the
        // web-root probe below cannot see them. Static asset types are public by definition and
        // never carry tenant data, so a known asset extension is tenant-agnostic. The extension
        // is taken from the path segment only, ignoring any query string or fragment. Tenant
        // content that merely looks like an asset (the /files upload route) is excluded: those
        // paths end in the uploaded file's own extension and are tenant data.
        if (!MatchesAnyPrefix(value, TenantScopedAssetLookalikePrefixes) && HasStaticAssetExtension(value))
        {
            return true;
        }

        // A request that maps to a real static file (favicon, css, js, fonts, wasm, …) is not
        // tenant-scoped. Checked through the file provider rather than a extension allowlist so
        // nothing can slip through on an unanticipated extension. A *directory* is never a file:
        // `Exists` alone is also true for directories, which would make a whole tree agnostic.
        var file = environment.WebRootFileProvider?.GetFileInfo(value);
        return file is { Exists: true, IsDirectory: false };
    }

    private static bool HasStaticAssetExtension(string value)
    {
        var query = value.IndexOfAny(['?', '#']);
        var pathOnly = query >= 0 ? value[..query] : value;
        var extension = Path.GetExtension(pathOnly);

        return extension.Length > 0 && StaticAssetExtensionSet.Contains(extension);
    }

    private static bool MatchesAnyPrefix(string path, IReadOnlyList<string> prefixes)
    {
        foreach (var prefix in prefixes)
        {
            if (MatchesPrefix(path, prefix))
            {
                return true;
            }
        }

        return false;
    }

    private static bool MatchesPrefix(string path, string prefix) =>
        path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
        (path.Length == prefix.Length || path[prefix.Length] == '/');
}
