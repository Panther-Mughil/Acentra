namespace Acentra.Domain.Entities;

public class StockMovement
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid ProductId { get; set; }
    public int Delta { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string? Note { get; set; }
    public DateTime OccurredUtc { get; set; }
}
