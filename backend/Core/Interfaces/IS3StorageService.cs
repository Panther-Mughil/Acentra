using System;
using System.IO;
using System.Threading.Tasks;

namespace Acentra.MultiTenant.Core.Interfaces;

public interface IS3StorageService
{
    Task<string> UploadFileAsync(string tenantCode, string fileCategory, string fileName, Stream content, string contentType);
    Task<string> GetPresignedDownloadUrlAsync(string tenantCode, string s3FileKey, int expiryMinutes = 15);
    Task<bool> DeleteFileAsync(string tenantCode, string s3FileKey);
}
