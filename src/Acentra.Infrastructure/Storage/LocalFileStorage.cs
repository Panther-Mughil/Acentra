using System.Globalization;
using System.Security.Cryptography;
using Acentra.Domain.Abstractions;

namespace Acentra.Infrastructure.Storage;

/// <summary>
/// Stores tenant files beneath a local root directory (default <c>storage/</c>, gitignored).
/// The key layout is identical to <see cref="S3FileStorage"/> — both go through
/// <see cref="StorageKeyBuilder"/> — so switching providers changes no caller and no row.
/// </summary>
public sealed class LocalFileStorage : IFileStorage
{
    private static readonly TimeSpan DefaultDownloadTtl = TimeSpan.FromMinutes(5);

    private readonly string _root;
    private readonly string _rootGuard;

    public LocalFileStorage(IStorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.LocalRoot);

        // Normalise once so every later containment check compares canonical, absolute paths.
        _root = Path.GetFullPath(options.LocalRoot);
        _rootGuard = _root.EndsWith(Path.DirectorySeparatorChar)
            ? _root
            : _root + Path.DirectorySeparatorChar;
    }

    public async Task<string> SaveAsync(Guid tenantId, Stream content, string fileName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(content);

        var key = StorageKeyBuilder.Build(tenantId, fileName);
        var path = ResolvePath(tenantId, key);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        await using var target = new FileStream(
            path, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 81920, useAsync: true);

        await content.CopyToAsync(target, ct).ConfigureAwait(false);

        return key;
    }

    public Task<Stream> OpenAsync(Guid tenantId, string key, CancellationToken ct)
    {
        var path = ResolvePath(tenantId, key);

        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"No stored object exists for key '{key}'.", path);
        }

        Stream stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 81920, useAsync: true);

        return Task.FromResult(stream);
    }

    public Task DeleteAsync(Guid tenantId, string key, CancellationToken ct)
    {
        var path = ResolvePath(tenantId, key);

        // Idempotent, matching S3 delete semantics: deleting an absent object is not an error.
        File.Delete(path);

        return Task.CompletedTask;
    }

    public Task<string> GetDownloadUrlAsync(Guid tenantId, string key, TimeSpan ttl, CancellationToken ct)
    {
        var normalized = StorageKeyBuilder.EnsureTenantKey(tenantId, key);

        // The filesystem has no presigning primitive; hand back a tokenised, expiring app-relative
        // URL instead so the contract shape is preserved. Serving it is the upload/download UI's job.
        var lifetime = ttl > TimeSpan.Zero ? ttl : DefaultDownloadTtl;
        var expires = DateTimeOffset.UtcNow
            .Add(lifetime)
            .ToUnixTimeSeconds()
            .ToString(CultureInfo.InvariantCulture);
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

        return Task.FromResult($"/files/{normalized}?expires={expires}&token={token}");
    }

    /// <summary>
    /// Maps a validated key onto an absolute path under the storage root, then re-proves the
    /// containment. This is the mandatory traversal guard: it rejects <c>..</c>, absolute keys and
    /// keys that would escape via a symbolic link.
    /// </summary>
    private string ResolvePath(Guid tenantId, string key)
    {
        var normalized = StorageKeyBuilder.EnsureTenantKey(tenantId, key);
        var relative = normalized.Replace('/', Path.DirectorySeparatorChar);
        var candidate = Path.GetFullPath(Path.Combine(_root, relative));

        if (!candidate.StartsWith(_rootGuard, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"The storage key '{key}' resolves outside the configured storage root.", nameof(key));
        }

        RejectSymbolicLinkEscape(candidate);

        return candidate;
    }

    /// <summary>
    /// Defence in depth on top of the prefix check: <see cref="Path.GetFullPath(string)"/> does not
    /// resolve links, so verify that neither the file itself nor any directory between it and the
    /// root is a symbolic link that would redirect the write or read outside the root.
    /// </summary>
    private void RejectSymbolicLinkEscape(string candidate)
    {
        var file = new FileInfo(candidate);
        if (file.Exists && file.LinkTarget is not null)
        {
            throw new ArgumentException(
                $"The storage key resolves to a symbolic link at '{candidate}'.", nameof(candidate));
        }

        var directory = Path.GetDirectoryName(candidate);
        while (!string.IsNullOrEmpty(directory) && directory.Length > _root.Length)
        {
            var info = new DirectoryInfo(directory);
            if (info.Exists && info.LinkTarget is not null)
            {
                throw new ArgumentException(
                    $"The storage key resolves through a symbolic link at '{directory}'.", nameof(candidate));
            }

            directory = Path.GetDirectoryName(directory);
        }
    }
}
