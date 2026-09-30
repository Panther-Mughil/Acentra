using System.Net;
using System.Net.Sockets;
using Acentra.Domain.Abstractions;
using Acentra.Infrastructure.Storage;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Util;
using Xunit.Abstractions;

namespace Acentra.IntegrationTests.Storage;

/// <summary>
/// REQ-004 §6: proves the S3/MinIO provider against a real MinIO server (the same one compose.yaml
/// starts), not merely a green build. The MinIO-backed facts self-skip when the server is absent so
/// the suite stays runnable offline; set <c>ACENTRA_MINIO_TESTS=1</c> to make them mandatory.
/// </summary>
public sealed class S3FileStorageMinioTests
{
    private static readonly Guid Tenant = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid OtherTenant = Guid.Parse("99999999-8888-7777-6666-555555555555");

    private readonly ITestOutputHelper _output;

    public S3FileStorageMinioTests(ITestOutputHelper output) => _output = output;

    [Theory]
    [InlineData("tenants/99999999888877776666555555555555/images/outside.png")]
    [InlineData("tenants/11111111222233334444555555555555/images/../../escape.png")]
    [InlineData("/etc/passwd")]
    [InlineData("../escape.png")]
    public async Task S3Provider_RejectsCrossTenantAndTraversalKeys(string key)
    {
        // The tenant-prefix guard runs before any network call, so this holds even without MinIO.
        using var storage = new S3FileStorage(CreateOptions());

        await Assert.ThrowsAsync<ArgumentException>(() => storage.OpenAsync(Tenant, key, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => storage.DeleteAsync(Tenant, key, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            () => storage.GetDownloadUrlAsync(Tenant, key, TimeSpan.FromMinutes(1), CancellationToken.None));
    }

    [Fact]
    public async Task RoundTrip_AgainstRealMinio_IsByteIdentical_AndPresignedUrlIsUsable()
    {
        if (!ShouldRunMinioFacts())
        {
            return;
        }

        var options = CreateOptions();
        using var storage = new S3FileStorage(options);
        var payload = RandomBytes(8192);

        await using var source = new MemoryStream(payload);
        var key = await storage.SaveAsync(Tenant, source, "minio round trip.bin", CancellationToken.None);
        _output.WriteLine($"[REQ-004] saved key: {key}");
        Assert.StartsWith(StorageKeyBuilder.TenantPrefix(Tenant) + "misc/", key, StringComparison.Ordinal);

        await using (var read = await storage.OpenAsync(Tenant, key, CancellationToken.None))
        {
            using var buffer = new MemoryStream();
            await read.CopyToAsync(buffer);
            Assert.Equal(payload, buffer.ToArray());
        }

        var url = await storage.GetDownloadUrlAsync(Tenant, key, TimeSpan.FromMinutes(2), CancellationToken.None);
        _output.WriteLine($"[REQ-004] presigned url: {url}");
        Assert.Contains($"/{options.Bucket}/{key}?", url, StringComparison.Ordinal);
        Assert.StartsWith("http://", url, StringComparison.Ordinal);

        // Fetch the presigned URL with a bare HttpClient — no credentials, no SDK.
        using var http = new HttpClient();
        var response = await http.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(payload, await response.Content.ReadAsByteArrayAsync());

        await storage.DeleteAsync(Tenant, key, CancellationToken.None);
        Assert.False(await ObjectExistsAsync(options.Bucket, key), "DeleteAsync must remove the object from the bucket.");
        await Assert.ThrowsAsync<FileNotFoundException>(
            () => storage.OpenAsync(Tenant, key, CancellationToken.None));

        _output.WriteLine($"[REQ-004] Save/Open/Url/Delete round-trip OK for {key}");
    }

    [Fact]
    public async Task OneBuilderKey_WorksUnchangedAgainstBothProviders_WithByteIdenticalContent()
    {
        if (!ShouldRunMinioFacts())
        {
            return;
        }

        var options = CreateOptions();
        var payload = RandomBytes(2048);

        // ONE key string, minted once by the shared builder, is used verbatim against both providers.
        var key = StorageKeyBuilder.Build(Tenant, "parity.pdf");
        Assert.StartsWith(StorageKeyBuilder.TenantPrefix(Tenant) + "documents/", key, StringComparison.Ordinal);
        _output.WriteLine($"[REQ-004] shared key: {key}");

        var localRoot = Path.Combine(Path.GetTempPath(), "acentra-parity-" + Guid.NewGuid().ToString("N"));
        try
        {
            var local = new LocalFileStorage(new StorageOptions { Provider = "Local", LocalRoot = localRoot });
            var localPath = Path.Combine(localRoot, key.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
            await File.WriteAllBytesAsync(localPath, payload, CancellationToken.None);

            await using (var read = await local.OpenAsync(Tenant, key, CancellationToken.None))
            {
                using var buffer = new MemoryStream();
                await read.CopyToAsync(buffer);
                Assert.Equal(payload, buffer.ToArray());
            }

            Assert.StartsWith(
                $"/files/{key}?",
                await local.GetDownloadUrlAsync(Tenant, key, TimeSpan.FromMinutes(1), CancellationToken.None),
                StringComparison.Ordinal);

            // Seed the identical key in MinIO through the real S3 client, then read it via the provider.
            await SeedObjectAsync(options.Bucket, key, payload);

            using var storage = new S3FileStorage(options);
            await using (var read = await storage.OpenAsync(Tenant, key, CancellationToken.None))
            {
                using var buffer = new MemoryStream();
                await read.CopyToAsync(buffer);
                Assert.Equal(payload, buffer.ToArray());
            }

            Assert.Contains(
                $"/{options.Bucket}/{key}?",
                await storage.GetDownloadUrlAsync(Tenant, key, TimeSpan.FromMinutes(1), CancellationToken.None),
                StringComparison.Ordinal);

            await storage.DeleteAsync(Tenant, key, CancellationToken.None);
            Assert.False(await ObjectExistsAsync(options.Bucket, key));

            await local.DeleteAsync(Tenant, key, CancellationToken.None);
            Assert.False(File.Exists(localPath));

            _output.WriteLine($"[REQ-004] identical key '{key}' round-tripped on both providers.");
        }
        finally
        {
            if (Directory.Exists(localRoot))
            {
                Directory.Delete(localRoot, recursive: true);
            }
        }
    }

    private static StorageOptions CreateOptions()
    {
        var endpoint = Environment.GetEnvironmentVariable("ACENTRA_MINIO_ENDPOINT") ?? "http://localhost:9000";
        return new StorageOptions
        {
            Provider = "S3",
            Endpoint = endpoint,
            Bucket = Environment.GetEnvironmentVariable("ACENTRA_MINIO_BUCKET") ?? "acentra-tenant-files",
            AccessKey = Environment.GetEnvironmentVariable("MINIO_ROOT_USER") ?? "acentra",
            SecretKey = Environment.GetEnvironmentVariable("MINIO_ROOT_PASSWORD") ?? "acentra_dev_password",
            UseSsl = false,
            Region = "us-east-1",
        };
    }

    private static AmazonS3Client CreateClient(StorageOptions options) =>
        new(
            new BasicAWSCredentials(options.AccessKey, options.SecretKey),
            new AmazonS3Config
            {
                ServiceURL = options.Endpoint,
                ForcePathStyle = true,
                AuthenticationRegion = options.Region,
                UseHttp = !options.UseSsl,
            });

    private static async Task SeedObjectAsync(string bucket, string key, byte[] payload)
    {
        using var client = CreateClient(CreateOptions());
        if (!await AmazonS3Util.DoesS3BucketExistV2Async(client, bucket))
        {
            await client.PutBucketAsync(bucket, CancellationToken.None);
        }

        await client.PutObjectAsync(
            new PutObjectRequest
            {
                BucketName = bucket,
                Key = key,
                InputStream = new MemoryStream(payload),
            },
            CancellationToken.None);
    }

    private static async Task<bool> ObjectExistsAsync(string bucket, string key)
    {
        using var client = CreateClient(CreateOptions());
        try
        {
            await client.GetObjectMetadataAsync(bucket, key, CancellationToken.None);
            return true;
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    private static bool ShouldRunMinioFacts()
    {
        var required = string.Equals(
            Environment.GetEnvironmentVariable("ACENTRA_MINIO_TESTS"), "1", StringComparison.Ordinal);
        var reachable = MinioReachable(CreateOptions().Endpoint);

        if (required && !reachable)
        {
            throw new InvalidOperationException(
                $"ACENTRA_MINIO_TESTS=1 but MinIO is unreachable at {CreateOptions().Endpoint}.");
        }

        return reachable;
    }

    private static bool MinioReachable(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
        {
            return false;
        }

        try
        {
            using var client = new TcpClient();
            if (!client.ConnectAsync(uri.Host, uri.Port).Wait(TimeSpan.FromMilliseconds(1000)))
            {
                return false;
            }

            return client.Connected;
        }
        catch
        {
            return false;
        }
    }

    private static byte[] RandomBytes(int length)
    {
        var bytes = new byte[length];
        Random.Shared.NextBytes(bytes);
        return bytes;
    }
}
