namespace Acentra.Domain.Entities;

public class TenantFile
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string Key { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public DateTime UploadedUtc { get; set; }
}
