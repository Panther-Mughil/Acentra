using Acentra.Domain.Abstractions;

namespace Acentra.Infrastructure.Storage;

public sealed class S3FileStorage : IFileStorage
{
    public Task<string> SaveAsync(Guid tenantId, Stream content, string fileName, CancellationToken ct)
    {
        throw new NotImplementedException();
    }

    public Task<Stream> OpenAsync(Guid tenantId, string key, CancellationToken ct)
    {
        throw new NotImplementedException();
    }

    public Task DeleteAsync(Guid tenantId, string key, CancellationToken ct)
    {
        throw new NotImplementedException();
    }

    public Task<string> GetDownloadUrlAsync(Guid tenantId, string key, TimeSpan ttl, CancellationToken ct)
    {
        throw new NotImplementedException();
    }
}
