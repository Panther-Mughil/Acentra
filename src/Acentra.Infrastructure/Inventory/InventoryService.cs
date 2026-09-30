using Acentra.Domain.Entities;

namespace Acentra.Infrastructure.Inventory;

public sealed class InventoryService : IInventoryService
{
    public Task<IReadOnlyList<Product>> ListProductsAsync(CancellationToken ct = default)
    {
        throw new NotImplementedException();
    }

    public Task<Product?> GetProductAsync(Guid id, CancellationToken ct = default)
    {
        throw new NotImplementedException();
    }

    public Task<Product> CreateProductAsync(ProductInput input, CancellationToken ct = default)
    {
        throw new NotImplementedException();
    }

    public Task<Product> UpdateProductAsync(Guid id, ProductInput input, CancellationToken ct = default)
    {
        throw new NotImplementedException();
    }

    public Task DeleteProductAsync(Guid id, CancellationToken ct = default)
    {
        throw new NotImplementedException();
    }

    public Task<StockLevel> AdjustStockAsync(
        Guid productId,
        int delta,
        string reason,
        string? note,
        CancellationToken ct = default)
    {
        throw new NotImplementedException();
    }

    public Task<IReadOnlyList<StockMovement>> ListMovementsAsync(
        Guid productId,
        CancellationToken ct = default)
    {
        throw new NotImplementedException();
    }
}
