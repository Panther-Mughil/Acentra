namespace Acentra.Infrastructure.Storage;

public interface IStorageOptions
{
    string Provider { get; }
    string LocalRoot { get; }
    string Endpoint { get; }
    string Bucket { get; }
    string AccessKey { get; }
    string SecretKey { get; }
    bool UseSsl { get; }
    string Region { get; }
}
