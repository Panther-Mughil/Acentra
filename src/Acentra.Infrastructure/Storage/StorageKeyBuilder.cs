using System.Globalization;

namespace Acentra.Infrastructure.Storage;

/// <summary>
/// Builds and validates the object-key layout shared by every <c>IFileStorage</c> provider:
/// <c>tenants/{tenantId:N}/{category}/{guid:N}{ext}</c>, where <c>category</c> is one of
/// <c>images</c>, <c>documents</c> or <c>misc</c>. Keeping this in one place means swapping
/// providers changes no caller and no stored row.
/// </summary>
public static class StorageKeyBuilder
{
    /// <summary>Category used when the extension is missing or not on the allow-list.</summary>
    public const string DefaultCategory = "misc";

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".bmp", ".gif", ".jpeg", ".jpg", ".png", ".svg", ".webp"
    };

    private static readonly HashSet<string> DocumentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".csv", ".doc", ".docx", ".pdf", ".rtf", ".txt", ".xls", ".xlsx"
    };

    private static readonly HashSet<string> AllowedExtensions =
        new(ImageExtensions.Concat(DocumentExtensions), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Produces the storage key for a freshly uploaded file. The caller-supplied file name is only
    /// ever used to derive a safe category and extension — never as a path component.
    /// </summary>
    public static string Build(Guid tenantId, string fileName)
    {
        ArgumentException.ThrowIfNullOrEmpty(fileName);

        if (fileName.Contains('/', StringComparison.Ordinal) ||
            fileName.Contains('\\', StringComparison.Ordinal) ||
            Path.IsPathRooted(fileName) ||
            fileName is "." or "..")
        {
            throw new ArgumentException(
                "The file name must be a single, relative file name.", nameof(fileName));
        }

        var extension = SanitizeExtension(Path.GetExtension(fileName));
        var category = ImageExtensions.Contains(extension)
            ? "images"
            : DocumentExtensions.Contains(extension)
                ? "documents"
                : DefaultCategory;

        return $"{TenantPrefix(tenantId)}{category}/{Guid.NewGuid():N}{extension}";
    }

    /// <summary>The tenant-scoped key prefix, <c>tenants/{tenantId:N}/</c>.</summary>
    public static string TenantPrefix(Guid tenantId) =>
        $"tenants/{tenantId.ToString("N", CultureInfo.InvariantCulture)}/";

    /// <summary>
    /// Returns the normalised extension (including the leading dot) when it is on the allow-list,
    /// otherwise an empty string so the stored key simply omits it.
    /// </summary>
    public static string SanitizeExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return string.Empty;
        }

        if (!extension.StartsWith(".", StringComparison.Ordinal) || extension.Length > 11)
        {
            return string.Empty;
        }

        for (var index = 1; index < extension.Length; index++)
        {
            if (!char.IsAsciiLetterOrDigit(extension[index]))
            {
                return string.Empty;
            }
        }

        var normalized = extension.ToLowerInvariant();
        return AllowedExtensions.Contains(normalized) ? normalized : string.Empty;
    }

    /// <summary>
    /// Re-validates a key before every read, delete or presign. A key that is not inside the
    /// specified tenant's prefix — or that contains a path-traversal segment — is rejected, so a
    /// key belonging to another tenant can never be opened.
    /// </summary>
    public static string EnsureTenantKey(Guid tenantId, string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        var prefix = TenantPrefix(tenantId);
        if (!key.StartsWith(prefix, StringComparison.Ordinal) ||
            key.Contains('\\', StringComparison.Ordinal) ||
            key.Contains(':', StringComparison.Ordinal) ||
            Path.IsPathRooted(key) ||
            key.Split('/').Any(segment => segment is "" or "." or ".."))
        {
            throw new ArgumentException(
                "The storage key is invalid or belongs to another tenant.", nameof(key));
        }

        return key;
    }
}
