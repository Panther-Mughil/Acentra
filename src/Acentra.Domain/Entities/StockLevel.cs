namespace Acentra.Domain.Entities;

public class StockLevel
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid ProductId { get; set; }
    public int Quantity { get; set; }
    public DateTime UpdatedUtc { get; set; }
}
