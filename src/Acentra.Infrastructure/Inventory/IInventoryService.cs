using Acentra.Domain.Entities;

namespace Acentra.Infrastructure.Inventory;

public interface IInventoryService
{
    Task<IReadOnlyList<Product>> ListProductsAsync(CancellationToken ct = default);
    Task<Product?> GetProductAsync(Guid id, CancellationToken ct = default);
    Task<Product> CreateProductAsync(ProductInput input, CancellationToken ct = default);
    Task<Product> UpdateProductAsync(Guid id, ProductInput input, CancellationToken ct = default);
    Task DeleteProductAsync(Guid id, CancellationToken ct = default);
    Task<StockLevel> AdjustStockAsync(Guid productId, int delta, string reason, string? note, CancellationToken ct = default);
    Task<IReadOnlyList<StockMovement>> ListMovementsAsync(Guid productId, CancellationToken ct = default);
}
