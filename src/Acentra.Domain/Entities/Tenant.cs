namespace Acentra.Domain.Entities;

public class Tenant
{
    public Guid Id { get; set; }
    public string Slug { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string DatabaseName { get; set; } = string.Empty;
    public string Status { get; set; } = "Active";
    public DateTime CreatedUtc { get; set; }
}
