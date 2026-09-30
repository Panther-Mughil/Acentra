using Acentra.Web.Middleware;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;

namespace Acentra.IntegrationTests;

/// <summary>
/// Regression coverage for the static-asset allowlist fix. <c>MapStaticAssets</c> serves
/// fingerprinted assets (<c>/app.4j8wku364b.css</c>) from a virtual route: the URL is in the
/// endpoint manifest but there is no file on disk, so the web-root file-provider probe cannot
/// see it. Before the fix those URLs were classified as tenant-scoped and the middleware
/// answered 302/400 instead of the stylesheet. These tests pin the classification itself, and
/// pin that ordinary tenant-scoped routes stay fail-closed.
/// </summary>
public sealed class StaticAssetAllowlistTests
{
    private static readonly IWebHostEnvironment EmptyWebRoot = new StubWebEnvironment();

    [Theory]
    [InlineData("/app.deadbeef01.css")]
    [InlineData("/app.4j8wku364b.css?v=20260930")]
    [InlineData("/app.4j8wku364b.css#fragment")]
    [InlineData("/Acentra.Web.styles.css")]
    [InlineData("/app.css")]
    [InlineData("/_framework/blazor.web.js")]
    [InlineData("/lib/bootstrap/dist/css/bootstrap.min.css")]
    [InlineData("/images/tenant-logo.PNG")]
    [InlineData("/fonts/inter.woff2")]
    [InlineData("/favicon.ico")]
    [InlineData("/favicon.svg")]
    [InlineData("/manifest.webmanifest")]
    [InlineData("/")]
    public void Static_asset_paths_are_tenant_agnostic(string path)
    {
        Assert.True(TenantAgnosticPaths.IsTenantAgnostic(new PathString(path), EmptyWebRoot),
            $"'{path}' must be tenant-agnostic (public static asset or allowlisted path).");
    }

    [Theory]
    [InlineData("/inventory")]
    [InlineData("/inventory/products")]
    [InlineData("/orders")]
    [InlineData("/suppliers")]
    [InlineData("/settings")]
    [InlineData("/dashboard")]
    [InlineData("/app")]
    [InlineData("/download/report.weird")]
    [InlineData("/files/tenant-a/secret.pdf.ext")]
    [InlineData("/files/tenants/00000000000000000000000000000000/documents/note.txt")]
    [InlineData("/files/tenants/00000000000000000000000000000000/images/scan.png")]
    public void Tenant_scoped_routes_are_not_tenant_agnostic(string path)
    {
        Assert.False(TenantAgnosticPaths.IsTenantAgnostic(new PathString(path), EmptyWebRoot),
            $"'{path}' must stay tenant-scoped (fail closed).");
    }

    [Fact]
    public void Allowlist_does_not_treat_a_bare_directory_as_a_static_file()
    {
        // A path that merely *contains* an asset extension is not an asset; the extension must be
        // the final segment's suffix.
        Assert.False(TenantAgnosticPaths.IsTenantAgnostic(new PathString("/inventory.css/list"), EmptyWebRoot));
    }

    [Fact]
    public void Tenant_uploaded_files_are_never_excused_by_their_extension()
    {
        // Walk the whole static-asset extension list: a tenant upload served from /files must stay
        // tenant-scoped whatever extension it happens to have, so the /files route fails closed.
        foreach (var extension in TenantAgnosticPaths.StaticAssetExtensions)
        {
            var path = new PathString($"/files/tenants/00000000000000000000000000000000/documents/file{extension}");

            Assert.False(TenantAgnosticPaths.IsTenantAgnostic(path, EmptyWebRoot),
                $"'{path}' must stay tenant-scoped even though '.{extension}' is a static-asset extension.");
        }
    }

    private sealed class StubWebEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "Acentra.Web";

        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();

        public string WebRootPath { get; set; } = string.Empty;

        public string EnvironmentName { get; set; } = "Development";

        public string ContentRootPath { get; set; } = string.Empty;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
