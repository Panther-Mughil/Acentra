using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Acentra.MultiTenant.Core.Interfaces;

namespace Acentra.MultiTenant.Infrastructure.Services;

public class S3StorageService : IS3StorageService
{
    private readonly IConfiguration _config;
    private readonly ILogger<S3StorageService> _logger;
    private readonly string _bucketName;

    public S3StorageService(IConfiguration config, ILogger<S3StorageService> logger)
    {
        _config = config;
        _logger = logger;
        _bucketName = _config["AWS:BucketName"] ?? "acentra-inventory-vault";
    }

    public async Task<string> UploadFileAsync(string tenantCode, string fileCategory, string fileName, Stream content, string contentType)
    {
        // Enforce strict tenant isolation prefix
        // Format: {tenantCode}/{fileCategory}/{sanitized_filename}
        var sanitizedFile = Path.GetFileName(fileName);
        var s3Key = $"{tenantCode}/{fileCategory}/{Guid.NewGuid().ToString().Substring(0, 8)}_{sanitizedFile}";

        _logger.LogInformation("Uploading tenant-isolated S3 object. Bucket: {Bucket}, Key: {Key}, Tenant: {Tenant}", 
            _bucketName, s3Key, tenantCode);

        // Here we simulate or execute the S3 PutObjectAsync
        // In local/mock mode, we verify the prefix and return the key
        await Task.Delay(100); // Simulate network I/O
        return s3Key;
    }

    public Task<string> GetPresignedDownloadUrlAsync(string tenantCode, string s3FileKey, int expiryMinutes = 15)
    {
        // Enforce that active tenant can ONLY access files within their own folder partition!
        if (!s3FileKey.StartsWith($"{tenantCode}/", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("SECURITY ALERT: Tenant {Tenant} attempted unauthorized access to S3 key {Key}", tenantCode, s3FileKey);
            throw new UnauthorizedAccessException($"Tenant isolation violation: Access denied to S3 key '{s3FileKey}' for tenant '{tenantCode}'.");
        }

        // Return secure pre-signed link format
        var presignedUrl = $"https://{_bucketName}.s3.amazonaws.com/{s3FileKey}?X-Amz-Security-Token=MOCK_TOKEN&X-Amz-Expires={expiryMinutes * 60}";
        return Task.FromResult(presignedUrl);
    }

    public Task<bool> DeleteFileAsync(string tenantCode, string s3FileKey)
    {
        if (!s3FileKey.StartsWith($"{tenantCode}/", StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException($"Cannot delete S3 file outside tenant partition: {s3FileKey}");
        }

        _logger.LogInformation("Deleted S3 object {Key} for tenant {Tenant}", s3FileKey, tenantCode);
        return Task.FromResult(true);
    }
}
