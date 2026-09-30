using Acentra.Domain.Abstractions;
using Acentra.Domain.Entities;
using Acentra.Infrastructure.TenantData;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;

namespace Acentra.Web.Components;

/// <summary>
/// Serves uploaded tenant files at <c>/files/{**key}</c> — the app-relative URL that
/// <c>LocalFileStorage.GetDownloadUrlAsync</c> hands back (REQ-004 left the serving side to the UI).
///
/// <para>
/// Authorization follows architecture §6 exactly:
/// </para>
/// <list type="number">
///   <item>the current tenant is taken from <see cref="ITenantContext"/> — the resolved, re-authorized
///         circuit/request tenant. It is <em>never</em> read from the URL;</item>
///   <item>the key is never trusted: the object is resolved through the tenant-scoped
///         <c>TenantFile</c> row first. That query carries the global tenant filter and runs against
///         that tenant's database, so a key belonging to another tenant is simply absent;</item>
///   <item>only then is the object streamed through <see cref="IFileStorage"/>, which re-validates
///         that the key carries this tenant's <c>tenants/{tenantId}/</c> prefix.</item>
/// </list>
///
/// <para>
/// The <c>expires</c>/<c>token</c> query parameters the local provider appends are <em>not</em>
/// authority and are ignored: authorization is the resolving tenant plus the database row. A missing
/// object and another tenant's object produce the identical, empty <c>404</c>, so the endpoint cannot
/// be used to discover whether a key exists elsewhere.
/// </para>
///
/// <para>
/// This type lives next to the inventory pages because the page that uploads files is what produces
/// these URLs; it is declared in the <c>Acentra.Web.Components</c> namespace (which <c>Program.cs</c>
/// already imports) so registering the route needs no extra <c>using</c> there. The route itself is
/// registered from <c>Program.cs</c> with a single line: <c>app.MapInventoryFileDownloads();</c>.
/// </para>
/// </summary>
public static class InventoryFileEndpoints
{
    /// <summary>The route pattern; a catch-all so a full <c>tenants/{id}/category/file</c> key fits.</summary>
    public const string Pattern = "/files/{**key}";

    /// <summary>
    /// Maps the download endpoint. Requires a tenant (the path is not on the tenant-agnostic
    /// allowlist, so <c>TenantResolutionMiddleware</c> has already failed a tenant-less request) and
    /// an authenticated caller.
    /// </summary>
    public static IEndpointRouteBuilder MapInventoryFileDownloads(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet(Pattern, DownloadAsync)
            .RequireAuthorization()
            .WithName("InventoryFileDownload");

        return endpoints;
    }

    private static async Task DownloadAsync(
        string key,
        HttpContext http,
        ITenantContext tenant,
        ITenantDbContextFactory contextFactory,
        IFileStorage fileStorage,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("Acentra.Web.InventoryFileDownload");
        var ct = http.RequestAborted;

        if (!tenant.IsResolved)
        {
            // Fail closed: there is no tenant to resolve a row against and no prefix to stream from.
            logger.LogWarning("Refused a file download: no tenant is resolved.");
            http.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        if (string.IsNullOrWhiteSpace(key))
        {
            http.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        TenantFile? file;

        try
        {
            await using var db = contextFactory.Create();

            // The row is the authority, and the query filter + this tenant's database scope it: a key
            // that belongs to another tenant cannot be found here.
            file = await db.Files
                .AsNoTracking()
                .FirstOrDefaultAsync(candidate => candidate.Key == key, ct);
        }
        catch (ArgumentException)
        {
            // A malformed key that EF/PostgreSQL rejects is a not-found, never an error page.
            file = null;
        }

        if (file is null)
        {
            logger.LogWarning(
                "Refused a file download for tenant '{Slug}': no such file row.", tenant.Slug);
            http.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        Stream content;

        try
        {
            // Storage re-validates the tenants/{tenantId}/ prefix, so the key cannot escape the
            // tenant even if a row were somehow wrong.
            content = await fileStorage.OpenAsync(tenant.TenantId, file.Key, ct);
        }
        catch (Exception exception) when (exception is ArgumentException or FileNotFoundException)
        {
            logger.LogWarning(
                exception,
                "Refused a file download for tenant '{Slug}': the stored object is unavailable.",
                tenant.Slug);
            http.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        http.Response.ContentType = string.IsNullOrWhiteSpace(file.ContentType)
            ? "application/octet-stream"
            : file.ContentType;
        http.Response.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment")
        {
            FileNameStar = file.FileName
        }.ToString();

        await using (content)
        {
            await content.CopyToAsync(http.Response.Body, ct);
        }
    }
}
