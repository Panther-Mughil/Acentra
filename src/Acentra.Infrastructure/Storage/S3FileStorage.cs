using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Util;
using Acentra.Domain.Abstractions;

namespace Acentra.Infrastructure.Storage;

/// <summary>
/// S3-compatible provider. Configuration targets MinIO by default (path-style addressing, HTTP),
/// but the same code path speaks straight to AWS S3 when <c>Endpoint</c>/<c>UseSsl</c> point there.
/// </summary>
public sealed class S3FileStorage : IFileStorage, IDisposable
{
    private static readonly TimeSpan DefaultDownloadTtl = TimeSpan.FromMinutes(5);

    private readonly IStorageOptions _options;
    private readonly IAmazonS3 _client;
    private readonly SemaphoreSlim _bootstrapGate = new(1, 1);
    private volatile bool _bucketReady;
    private bool _disposed;

    public S3FileStorage(IStorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Bucket);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Region);

        _options = options;

        var config = new AmazonS3Config
        {
            ServiceURL = options.Endpoint,
            // MinIO serves buckets as path segments, not subdomains — required, not optional.
            ForcePathStyle = true,
            AuthenticationRegion = options.Region,
            UseHttp = !options.UseSsl,
        };

        _client = new AmazonS3Client(
            new BasicAWSCredentials(options.AccessKey, options.SecretKey),
            config);
    }

    public async Task<string> SaveAsync(Guid tenantId, Stream content, string fileName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(content);

        var key = StorageKeyBuilder.Build(tenantId, fileName);
        await EnsureBucketAsync(ct).ConfigureAwait(false);

        var request = new PutObjectRequest
        {
            BucketName = _options.Bucket,
            Key = key,
            InputStream = content,
            AutoCloseStream = false,
            // A cleartext (MinIO) endpoint rejects the AWS streaming, chunked body signature, so
            // send a single signed body there instead. TLS endpoints keep the SDK default.
            UseChunkEncoding = _options.UseSsl,
        };

        try
        {
            await _client.PutObjectAsync(request, ct).ConfigureAwait(false);
        }
        catch (AmazonS3Exception exception) when (IsSignatureMismatch(exception) && _options.UseSsl && content.CanSeek)
        {
            // Last-resort MinIO workaround. The SDK only permits disabling payload signing over
            // HTTPS, hence the UseSsl guard above; rewind and retry once with it switched off.
            // A non-seekable stream cannot be replayed, so it surfaces the original error instead.
            content.Position = 0;
            request.UseChunkEncoding = false;
            request.DisablePayloadSigning = true;
            await _client.PutObjectAsync(request, ct).ConfigureAwait(false);
        }

        return key;
    }

    public async Task<Stream> OpenAsync(Guid tenantId, string key, CancellationToken ct)
    {
        var normalized = StorageKeyBuilder.EnsureTenantKey(tenantId, key);
        await EnsureBucketAsync(ct).ConfigureAwait(false);

        try
        {
            var response = await _client.GetObjectAsync(_options.Bucket, normalized, ct).ConfigureAwait(false);
            return response.ResponseStream;
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            throw new FileNotFoundException(
                $"No stored object exists for key '{normalized}'.", normalized, exception);
        }
    }

    public async Task DeleteAsync(Guid tenantId, string key, CancellationToken ct)
    {
        var normalized = StorageKeyBuilder.EnsureTenantKey(tenantId, key);
        await EnsureBucketAsync(ct).ConfigureAwait(false);

        await _client.DeleteObjectAsync(_options.Bucket, normalized, ct).ConfigureAwait(false);
    }

    public async Task<string> GetDownloadUrlAsync(Guid tenantId, string key, TimeSpan ttl, CancellationToken ct)
    {
        var normalized = StorageKeyBuilder.EnsureTenantKey(tenantId, key);
        await EnsureBucketAsync(ct).ConfigureAwait(false);

        var lifetime = ttl > TimeSpan.Zero ? ttl : DefaultDownloadTtl;
        var request = new GetPreSignedUrlRequest
        {
            BucketName = _options.Bucket,
            Key = normalized,
            Verb = HttpVerb.GET,
            Expires = DateTime.UtcNow.Add(lifetime),
            // The presigner defaults to https regardless of AmazonS3Config.UseHttp, which would make
            // every URL unusable against a cleartext endpoint such as local MinIO.
            Protocol = _options.UseSsl ? Protocol.HTTPS : Protocol.HTTP,
        };

        return _client.GetPreSignedURL(request);
    }

    /// <summary>
    /// Creates the bucket once, idempotently, on first use. A misconfigured or unreachable MinIO
    /// fails loudly here — there is deliberately no silent local fallback.
    /// </summary>
    private async Task EnsureBucketAsync(CancellationToken ct)
    {
        if (_bucketReady)
        {
            return;
        }

        await _bootstrapGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_bucketReady)
            {
                return;
            }

            if (!await AmazonS3Util.DoesS3BucketExistV2Async(_client, _options.Bucket).ConfigureAwait(false))
            {
                await _client.PutBucketAsync(_options.Bucket, ct).ConfigureAwait(false);
            }

            _bucketReady = true;
        }
        finally
        {
            _bootstrapGate.Release();
        }
    }

    private static bool IsSignatureMismatch(AmazonS3Exception exception) =>
        string.Equals(exception.ErrorCode, "SignatureDoesNotMatch", StringComparison.OrdinalIgnoreCase) ||
        exception.Message.Contains("SignatureDoesNotMatch", StringComparison.OrdinalIgnoreCase);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _bootstrapGate.Dispose();
        _client.Dispose();
    }
}
