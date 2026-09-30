namespace Acentra.Infrastructure.Inventory;

public sealed record ProductInput(
    string Sku,
    string Name,
    string? Description,
    decimal UnitPrice,
    int ReorderLevel,
    bool IsActive);
