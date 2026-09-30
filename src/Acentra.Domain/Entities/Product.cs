namespace Acentra.Domain.Entities;

public class Product
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string Sku { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public decimal UnitPrice { get; set; }
    public int ReorderLevel { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
}
