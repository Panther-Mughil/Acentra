using System;

namespace Acentra.MultiTenant.Core.Entities;

public class StockTransaction : IMultiTenant
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    
    public Guid InventoryItemId { get; set; }
    public string ItemSku { get; set; } = string.Empty;
    public string ItemName { get; set; } = string.Empty;
    
    public string TransactionType { get; set; } = "INBOUND"; // INBOUND, OUTBOUND, AUDIT_CORRECTION
    public int QuantityChanged { get; set; }
    public int ResultingStock { get; set; }
    public string Reason { get; set; } = string.Empty;
    
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

public class TenantAuditLog : IMultiTenant
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    
    public string Action { get; set; } = string.Empty;
    public string Details { get; set; } = string.Empty;
    public string IpAddress { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}
