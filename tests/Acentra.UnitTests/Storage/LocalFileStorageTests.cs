using Acentra.Infrastructure.Storage;

namespace Acentra.UnitTests.Storage;

/// <summary>
/// REQ-004 §4/§6: the local filesystem provider round-trips byte-identically, creates directories
/// on demand, and — critically — refuses any key that would escape its configured root.
/// </summary>
public sealed class LocalFileStorageTests : IDisposable
{
    private static readonly Guid Tenant = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid OtherTenant = Guid.Parse("99999999-8888-7777-6666-555555555555");

    private readonly string _sandbox;
    private readonly string _root;
    private readonly LocalFileStorage _storage;

    public LocalFileStorageTests()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "acentra-storage-" + Guid.NewGuid().ToString("N"));
        _root = Path.Combine(_sandbox, "root");
        Directory.CreateDirectory(_root);

        _storage = new LocalFileStorage(new StorageOptions { Provider = "Local", LocalRoot = _root });
    }

    public void Dispose()
    {
        if (Directory.Exists(_sandbox))
        {
            Directory.Delete(_sandbox, recursive: true);
        }
    }

    [Fact]
    public async Task SaveThenOpen_ReturnsByteIdenticalContent()
    {
        var payload = RandomBytes(4096);
        await using var source = new MemoryStream(payload);

        var key = await _storage.SaveAsync(Tenant, source, "quarterly report.pdf", CancellationToken.None);

        Assert.StartsWith(StorageKeyBuilder.TenantPrefix(Tenant) + "documents/", key, StringComparison.Ordinal);
        Assert.True(File.Exists(PhysicalPath(key)), $"Expected the object to exist at {PhysicalPath(key)}");

        await using var read = await _storage.OpenAsync(Tenant, key, CancellationToken.None);
        using var buffer = new MemoryStream();
        await read.CopyToAsync(buffer);

        Assert.Equal(payload, buffer.ToArray());
    }

    [Fact]
    public async Task Save_AllowsAZeroByteUpload()
    {
        await using var source = new MemoryStream();

        var key = await _storage.SaveAsync(Tenant, source, "empty.txt", CancellationToken.None);

        await using var read = await _storage.OpenAsync(Tenant, key, CancellationToken.None);
        Assert.Equal(0, read.Length);
    }

    [Fact]
    public async Task Delete_RemovesTheObject_AndIsIdempotent()
    {
        await using var source = new MemoryStream(RandomBytes(64));
        var key = await _storage.SaveAsync(Tenant, source, "note.txt", CancellationToken.None);

        await _storage.DeleteAsync(Tenant, key, CancellationToken.None);

        Assert.False(File.Exists(PhysicalPath(key)));
        Assert.Throws<FileNotFoundException>(() => _storage.OpenAsync(Tenant, key, CancellationToken.None).GetAwaiter().GetResult());

        // Deleting twice must not throw — S3 delete semantics are idempotent, so the local provider matches.
        await _storage.DeleteAsync(Tenant, key, CancellationToken.None);
    }

    [Fact]
    public async Task Open_AbsentKey_ThrowsFileNotFound()
    {
        var missing = $"{StorageKeyBuilder.TenantPrefix(Tenant)}documents/{Guid.NewGuid():N}.pdf";

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => _storage.OpenAsync(Tenant, missing, CancellationToken.None));
    }

    [Fact]
    public async Task GetDownloadUrl_ReturnsATokenisedRelativeUrl_AndClampsNonPositiveTtl()
    {
        await using var source = new MemoryStream(RandomBytes(16));
        var key = await _storage.SaveAsync(Tenant, source, "image.png", CancellationToken.None);

        var url = await _storage.GetDownloadUrlAsync(Tenant, key, TimeSpan.Zero, CancellationToken.None);

        Assert.StartsWith($"/files/{key}?", url, StringComparison.Ordinal);
        Assert.Contains("token=", url, StringComparison.Ordinal);

        var expires = long.Parse(
            url.Split("expires=")[1].Split('&')[0],
            System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(expires > DateTimeOffset.UtcNow.ToUnixTimeSeconds(), "A non-positive TTL must clamp to a future expiry.");
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("../../escape.txt")]
    [InlineData("/etc/passwd")]
    [InlineData("tenants/99999999888877776666555555555555/images/outside.png")]
    [InlineData("tenants/11111111222233334444555555555555/images/../../../escape.png")]
    public async Task ReadWriteDeletePaths_RejectTraversalAndCrossTenantKeys(string key)
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _storage.OpenAsync(Tenant, key, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            () => _storage.DeleteAsync(Tenant, key, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            () => _storage.GetDownloadUrlAsync(Tenant, key, TimeSpan.FromMinutes(1), CancellationToken.None));

        AssertOnlyRootExistsInSandbox();
    }

    [Theory]
    [InlineData("../escape.png")]
    [InlineData("../../escape.png")]
    [InlineData("/etc/passwd")]
    [InlineData("nested/file.png")]
    public async Task Save_RejectsTraversalFileNames_AndWritesNothingOutsideTheRoot(string fileName)
    {
        await using var source = new MemoryStream(RandomBytes(8));

        await Assert.ThrowsAsync<ArgumentException>(
            () => _storage.SaveAsync(Tenant, source, fileName, CancellationToken.None));

        AssertOnlyRootExistsInSandbox();
    }

    [Fact]
    public async Task Open_RejectsAKeyThatEscapesThroughASymbolicLink()
    {
        var tenantDirectory = Path.Combine(_root, "tenants", Tenant.ToString("N"));
        var redirect = Path.Combine(tenantDirectory, "documents");
        var escapeTarget = Path.Combine(_sandbox, "escape-target");
        Directory.CreateDirectory(escapeTarget);
        Directory.CreateDirectory(tenantDirectory);
        File.CreateSymbolicLink(redirect, escapeTarget);

        var key = $"{StorageKeyBuilder.TenantPrefix(Tenant)}documents/leaked.png";

        await Assert.ThrowsAsync<ArgumentException>(
            () => _storage.OpenAsync(Tenant, key, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            () => _storage.SaveAsync(Tenant, new MemoryStream(RandomBytes(8)), "leaked.txt", CancellationToken.None));
    }

    private string PhysicalPath(string key) =>
        Path.Combine(_root, key.Replace('/', Path.DirectorySeparatorChar));

    private void AssertOnlyRootExistsInSandbox()
    {
        var entries = Directory.GetFileSystemEntries(_sandbox);
        Assert.Equal([_root], entries);
    }

    private static byte[] RandomBytes(int length)
    {
        var bytes = new byte[length];
        Random.Shared.NextBytes(bytes);
        return bytes;
    }
}
