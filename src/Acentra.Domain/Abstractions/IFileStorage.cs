namespace Acentra.Domain.Abstractions;

public interface IFileStorage
{
    Task<string> SaveAsync(Guid tenantId, Stream content, string fileName, CancellationToken ct);
    Task<Stream> OpenAsync(Guid tenantId, string key, CancellationToken ct);
    Task DeleteAsync(Guid tenantId, string key, CancellationToken ct);
    Task<string> GetDownloadUrlAsync(Guid tenantId, string key, TimeSpan ttl, CancellationToken ct);
}
