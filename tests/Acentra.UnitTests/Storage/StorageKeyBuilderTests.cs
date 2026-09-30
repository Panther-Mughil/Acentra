using System.Text.RegularExpressions;
using Acentra.Infrastructure.Storage;

namespace Acentra.UnitTests.Storage;

/// <summary>
/// REQ-004 §4/§6: one key layout shared by both providers —
/// <c>tenants/{tenantId:N}/{category}/{guid:N}{ext}</c> — with strict name and extension
/// sanitisation and a tenant-prefix guard reused by every read/delete/presign path.
/// </summary>
public sealed class StorageKeyBuilderTests
{
    private static readonly Guid Tenant = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private static readonly Regex KeyShape = new(
        @"^tenants/(?<tenant>[0-9a-f]{32})/(?<category>images|documents|misc)/(?<id>[0-9a-f]{32})(?<ext>\.[a-z0-9]{1,10})?$",
        RegexOptions.Compiled);

    [Theory]
    [InlineData("photo.PNG", "images", ".png")]
    [InlineData("diagram.svg", "images", ".svg")]
    [InlineData("report.pdf", "documents", ".pdf")]
    [InlineData("notes.TXT", "documents", ".txt")]
    [InlineData("archive.zip", "misc", "")]
    [InlineData("no-extension", "misc", "")]
    [InlineData("sketch.psd", "misc", "")]
    public void Build_UsesTheSharedLayout(string fileName, string expectedCategory, string expectedExtension)
    {
        var key = StorageKeyBuilder.Build(Tenant, fileName);

        var match = KeyShape.Match(key);
        Assert.True(match.Success, $"Unexpected key shape: '{key}'");
        Assert.Equal(Tenant.ToString("N"), match.Groups["tenant"].Value);
        Assert.Equal(expectedCategory, match.Groups["category"].Value);
        Assert.Equal(expectedExtension, match.Groups["ext"].Value);
        Assert.DoesNotContain("..", key, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_GeneratesAFreshIdentifierPerCall()
    {
        var first = StorageKeyBuilder.Build(Tenant, "report.pdf");
        var second = StorageKeyBuilder.Build(Tenant, "report.pdf");

        Assert.NotEqual(first, second);
        Assert.StartsWith(StorageKeyBuilder.TenantPrefix(Tenant) + "documents/", first, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("../escape.png")]
    [InlineData("..\\escape.png")]
    [InlineData("/etc/passwd")]
    [InlineData("/tmp/../../escape.png")]
    [InlineData("nested/sub/file.png")]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("")]
    public void Build_RejectsFileNamesThatAreNotASingleRelativeName(string fileName)
    {
        Assert.Throws<ArgumentException>(() => StorageKeyBuilder.Build(Tenant, fileName));
    }

    [Theory]
    [InlineData(".png", ".png")]
    [InlineData(".PNG", ".png")]
    [InlineData("", "")]
    [InlineData(null, "")]
    [InlineData(".", "")]
    [InlineData(".p!g", "")]
    [InlineData(".exe", "")]
    [InlineData(".sh", "")]
    [InlineData(".shtml", "")]
    [InlineData(".thisiswaytoolong", "")]
    public void SanitizeExtension_OnlyAllowsShortAlphanumericAllowListedExtensions(
        string? extension,
        string expected)
    {
        Assert.Equal(expected, StorageKeyBuilder.SanitizeExtension(extension));
    }

    [Fact]
    public void EnsureTenantKey_AcceptsAKeyBuiltForTheSameTenant()
    {
        var key = StorageKeyBuilder.Build(Tenant, "report.pdf");

        Assert.Equal(key, StorageKeyBuilder.EnsureTenantKey(Tenant, key));
    }

    [Fact]
    public void EnsureTenantKey_RejectsAKeyBelongingToAnotherTenant()
    {
        var otherTenant = Guid.Parse("99999999-8888-7777-6666-555555555555");
        var key = StorageKeyBuilder.Build(otherTenant, "report.pdf");

        Assert.Throws<ArgumentException>(() => StorageKeyBuilder.EnsureTenantKey(Tenant, key));
    }

    [Theory]
    [InlineData("tenants/11111111222233334444555555555555/images/../../etc/passwd")]
    [InlineData("tenants/11111111222233334444555555555555/images/../../../outside.png")]
    [InlineData("tenants/11111111222233334444555555555555/../outside.png")]
    [InlineData("tenants/11111111222233334444555555555555/./outside.png")]
    [InlineData("tenants/11111111222233334444555555555555/images//outside.png")]
    [InlineData("/tenants/11111111222233334444555555555555/images/outside.png")]
    [InlineData("tenants\\11111111222233334444555555555555\\images\\outside.png")]
    [InlineData("tenants/11111111222233334444555555555555/images/")]
    public void EnsureTenantKey_RejectsTraversalAndAbsoluteKeys(string key)
    {
        Assert.Throws<ArgumentException>(() => StorageKeyBuilder.EnsureTenantKey(Tenant, key));
    }
}
