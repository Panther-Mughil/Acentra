namespace Acentra.Infrastructure.Storage;

public sealed class StorageOptions : IStorageOptions
{
    public string Provider { get; set; } = "S3";
    public string LocalRoot { get; set; } = "storage";
    public string Endpoint { get; set; } = "http://localhost:9000";
    public string Bucket { get; set; } = "acentra-tenant-files";
    public string AccessKey { get; set; } = "acentra";
    public string SecretKey { get; set; } = "acentra_dev_password";
    public bool UseSsl { get; set; }
    public string Region { get; set; } = "us-east-1";
}
