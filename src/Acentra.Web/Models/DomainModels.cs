namespace Acentra.Web.Models;

public class Tenant
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public string Region { get; set; } = string.Empty;
    public int BranchCount { get; set; }
    public List<Branch> Branches { get; set; } = new();
    public int ActiveUsersCount { get; set; }
    public decimal TotalStockValue { get; set; }
    public string Status { get; set; } = "Active";
    public string EncryptionKeyId { get; set; } = string.Empty;
    public string PrimaryContact { get; set; } = string.Empty;
}

public class Branch
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public int TotalItemsCount { get; set; }
    public int LowStockCount { get; set; }
}

public enum StockStatus
{
    Healthy,
    LowStock,
    Critical,
    Overstock
}

public class InventoryItem
{
    public string Id { get; set; } = string.Empty;
    public string Sku { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string BranchId { get; set; } = string.Empty;
    public string BranchName { get; set; } = string.Empty;
    public string Unit { get; set; } = "Units";
    public int CurrentStock { get; set; }
    public int ReorderLevel { get; set; }
    public int OptimalStock { get; set; }
    public decimal UnitCost { get; set; }
    public decimal UnitPrice { get; set; }
    public string SupplierId { get; set; } = string.Empty;
    public string SupplierName { get; set; } = string.Empty;
    public StockStatus Status { get; set; }
    public string StorageLocation { get; set; } = string.Empty;
    public string BatchNumber { get; set; } = string.Empty;
    public DateTime ExpiryDate { get; set; }
    public DateTime LastUpdated { get; set; }
    public string Description { get; set; } = string.Empty;
    public string TemperatureRequirement { get; set; } = "Ambient (15°C - 25°C)";
}

public class StockMovement
{
    public string Id { get; set; } = string.Empty;
    public string ItemId { get; set; } = string.Empty;
    public string ItemName { get; set; } = string.Empty;
    public string MovementType { get; set; } = "Received"; // Received, Consumed, Adjusted, Transferred
    public int Quantity { get; set; }
    public int PreviousStock { get; set; }
    public int NewStock { get; set; }
    public DateTime Timestamp { get; set; }
    public string PerformedBy { get; set; } = string.Empty;
    public string ReferenceNumber { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
}

public class Supplier
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string ContactPerson { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string Location { get; set; } = string.Empty;
    public int ActiveOrders { get; set; }
    public double DeliveryScore { get; set; } // e.g. 96.5%
    public double AvgLeadTimeDays { get; set; }
    public string Status { get; set; } = "Excellent"; // Excellent, Good, AtRisk
    public decimal TotalSpendYtd { get; set; }
    public double Rating { get; set; } = 4.8;
    public int TotalProductsSupplied { get; set; }
}

public enum OrderStatus
{
    Draft,
    PendingApproval,
    Approved,
    Ordered,
    PartiallyReceived,
    Completed,
    Cancelled
}

public class PurchaseOrder
{
    public string Id { get; set; } = string.Empty;
    public string PoNumber { get; set; } = string.Empty;
    public string SupplierId { get; set; } = string.Empty;
    public string SupplierName { get; set; } = string.Empty;
    public string BranchId { get; set; } = string.Empty;
    public string BranchName { get; set; } = string.Empty;
    public DateTime CreatedDate { get; set; }
    public DateTime ExpectedDelivery { get; set; }
    public decimal TotalAmount { get; set; }
    public OrderStatus Status { get; set; }
    public string Priority { get; set; } = "Normal"; // Normal, Urgent, Critical
    public List<PurchaseOrderItem> Items { get; set; } = new();
    public string CreatedBy { get; set; } = string.Empty;
    public string? ApprovedBy { get; set; }
    public string Notes { get; set; } = string.Empty;
}

public class PurchaseOrderItem
{
    public string ItemId { get; set; } = string.Empty;
    public string ItemName { get; set; } = string.Empty;
    public string Sku { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public decimal UnitCost { get; set; }
    public decimal TotalCost => Quantity * UnitCost;
}

public class DocumentRecord
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Category { get; set; } = "Invoices"; // Invoices, Purchase Orders, Compliance, Product Documents, Reports
    public string FileType { get; set; } = "PDF";
    public string FileSize { get; set; } = "1.2 MB";
    public string Owner { get; set; } = string.Empty;
    public DateTime UploadedAt { get; set; }
    public string SecurityBadge { get; set; } = "Tenant Protected";
    public string S3StoragePath { get; set; } = string.Empty;
    public string Sha256Hash { get; set; } = string.Empty;
}

public class AuditLog
{
    public string Id { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
    public string User { get; set; } = string.Empty;
    public string UserRole { get; set; } = "Platform Administrator";
    public string Action { get; set; } = string.Empty;
    public string Resource { get; set; } = string.Empty;
    public string ResourceId { get; set; } = string.Empty;
    public string IpAddress { get; set; } = string.Empty;
    public string Severity { get; set; } = "Info"; // Info, Warning, Critical
    public string Details { get; set; } = string.Empty;
    public string? PreviousState { get; set; }
    public string? NewState { get; set; }
}

public class AlertItem
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
    public string Severity { get; set; } = "Warning"; // Critical, Warning, Attention, Resolved
    public string Category { get; set; } = "Inventory"; // Security, Inventory, Orders, Compliance
    public bool IsRead { get; set; }
    public string ActionText { get; set; } = string.Empty;
    public string ActionUrl { get; set; } = string.Empty;
}

public class AIInsight
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Subtitle { get; set; } = string.Empty;
    public string Type { get; set; } = "Reorder"; // Reorder, Optimization, Security, SupplyRisk
    public string ImpactText { get; set; } = string.Empty;
    public string PotentialSaving { get; set; } = string.Empty;
    public string ItemName { get; set; } = string.Empty;
    public int CurrentStock { get; set; }
    public int EstimatedRemainingDays { get; set; }
    public int RecommendedOrderUnits { get; set; }
    public string ActionButtonText { get; set; } = "Review Recommendation";
}

public class SecurityTestStep
{
    public string StepNumber { get; set; } = string.Empty;
    public string TestName { get; set; } = string.Empty;
    public string SourceContext { get; set; } = string.Empty;
    public string TargetContext { get; set; } = string.Empty;
    public string ExpectedOutcome { get; set; } = "PASS";
    public string Status { get; set; } = "Pending"; // Pending, Running, Pass, Blocked
    public int DurationMs { get; set; }
    public string LogDetail { get; set; } = string.Empty;
}
