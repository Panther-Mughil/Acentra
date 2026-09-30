using Acentra.Infrastructure.Storage;

namespace Acentra.IntegrationTests;

/// <summary>
/// REQ-006 §4 boundary B9 — storage isolation.
///
/// Uses the committed <c>Local</c> provider with a temp root (never MinIO, never the real storage
/// root): a key minted for tenant A cannot be opened, deleted or presigned while acting as tenant
/// B, and a path-traversal key is rejected. Every rejection is paired with a positive control so
/// "everything throws" cannot masquerade as a pass.
/// </summary>
public sealed class StorageIsolationBoundaryTests : IDisposable
{
    private static readonly Guid TenantA = Guid.Parse("a1000000-0000-4000-8000-00000000aaaa");
    private static readonly Guid TenantB = Guid.Parse("b1000000-0000-4000-8000-00000000bbbb");

    private static readonly byte[] Payload = "tenant A's private document"u8.ToArray();

    private readonly string _sandbox;
    private readonly string _root;
    private readonly LocalFileStorage _storage;

    public StorageIsolationBoundaryTests()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "acentra-b9-" + Guid.NewGuid().ToString("N"));
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
    public async Task B9_StorageIsolation_TenantAsKeyCannotBeUsedByTenantB_AndTraversalIsRejected()
    {
        // 1. Positive control: tenant A can store and read its own object byte-for-byte.
        string key;
        await using (var source = new MemoryStream(Payload, writable: false))
        {
            key = await _storage.SaveAsync(TenantA, source, "private-document.txt", CancellationToken.None);
        }

        Assert.StartsWith(StorageKeyBuilder.TenantPrefix(TenantA), key, StringComparison.Ordinal);
        Assert.True(File.Exists(PhysicalPath(key)), "The object should exist under tenant A's prefix.");

        await AssertBytesAsync(TenantA, key, Payload);

        // 2. Acting as tenant B, tenant A's key cannot be opened, deleted or presigned.
        await Assert.ThrowsAsync<ArgumentException>(
            () => _storage.OpenAsync(TenantB, key, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            () => _storage.DeleteAsync(TenantB, key, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            () => _storage.GetDownloadUrlAsync(TenantB, key, TimeSpan.FromMinutes(1), CancellationToken.None));

        // 3. Path traversal / absolute keys are rejected for the owning tenant too.
        var prefix = StorageKeyBuilder.TenantPrefix(TenantA);
        foreach (var malicious in new[]
                 {
                     $"{prefix}../escape.txt",
                     $"{prefix}images/../../escape.png",
                     "/etc/passwd",
                     "../escape.txt"
                 })
        {
            await Assert.ThrowsAsync<ArgumentException>(
                () => _storage.OpenAsync(TenantA, malicious, CancellationToken.None));
        }

        // 4. Nothing escaped: the sandbox contains only the root, and A's object is still readable
        //    (the rejected calls left no side effect).
        Assert.Equal([_root], Directory.GetFileSystemEntries(_sandbox));
        await AssertBytesAsync(TenantA, key, Payload);
    }

    private async Task AssertBytesAsync(Guid tenantId, string key, byte[] expected)
    {
        await using var stored = await _storage.OpenAsync(tenantId, key, CancellationToken.None);
        using var buffer = new MemoryStream();
        await stored.CopyToAsync(buffer);

        Assert.Equal(expected, buffer.ToArray());
    }

    private string PhysicalPath(string key) =>
        Path.Combine(_root, key.Replace('/', Path.DirectorySeparatorChar));
}
