using System.Text.RegularExpressions;
using Acentra.Domain.Entities;
using Acentra.Infrastructure.TenantData;
using Microsoft.EntityFrameworkCore;

namespace Acentra.Infrastructure.Inventory;

/// <summary>
/// The tenant-scoped inventory service — the durable contract the (later, separate) frontend is
/// built on. It is deliberately UI-agnostic: no <c>HttpContext</c>, no component types, no
/// tenant filtering of its own.
///
/// <para>
/// Isolation comes from <see cref="ITenantDbContextFactory"/>: every method opens a
/// <em>short-lived</em> context with <c>Create()</c> inside a <see langword="using"/>, and none is
/// ever stored in a field. That is what makes the class safe in a Blazor circuit where the tenant
/// can change mid-session (architecture §4.1): a cached context would serve the previous tenant's
/// connection, filters and connection string.
/// </para>
///
/// <para>
/// A row belonging to another tenant is simply <em>not found</em>. Every read, update, delete and
/// stock adjustment goes through the global query filter (and, transiently, the one database per
/// tenant), so a cross-tenant id yields <see cref="KeyNotFoundException"/> — never a cross-tenant
/// read or write.
/// </para>
/// </summary>
public sealed partial class InventoryService(ITenantDbContextFactory contextFactory) : IInventoryService
{
    /// <summary>The one accepted SKU shape — uppercase alphanumerics and dashes, 2–32 chars.</summary>
    public const string SkuFormat = "^[A-Z0-9-]{2,32}$";

    /// <summary>Largest <see cref="Product.Name"/> the schema stores; validated up front for a friendly message.</summary>
    public const int NameMaxLength = 200;

    /// <summary>Largest <see cref="Product.Description"/>; validated up front for a friendly message.</summary>
    public const int DescriptionMaxLength = 2000;

    /// <summary>Stored length of <see cref="StockMovement.Reason"/> in the model configuration.</summary>
    public const int ReasonMaxLength = 200;

    // The same shape is enforced in the UI ([RegularExpression]) and here — the service is the
    // authority, the UI attribute only exists to catch it before a round-trip.
    [GeneratedRegex(SkuFormat, RegexOptions.CultureInvariant)]
    private static partial Regex SkuRegex();

    /// <inheritdoc />
    public async Task<IReadOnlyList<Product>> ListProductsAsync(CancellationToken ct = default)
    {
        using var db = contextFactory.Create();

        return await db.Products
            .AsNoTracking()
            .OrderBy(p => p.Sku)
            .ToListAsync(ct);
    }

    /// <inheritdoc />
    /// <remarks>A product owned by another tenant is indistinguishable from a missing one: <c>null</c>.</remarks>
    public async Task<Product?> GetProductAsync(Guid id, CancellationToken ct = default)
    {
        using var db = contextFactory.Create();

        return await db.Products
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == id, ct);
    }

    /// <inheritdoc />
    public async Task<Product> CreateProductAsync(ProductInput input, CancellationToken ct = default)
    {
        // Validate before touching the database: the cheap, friendly failure must never depend on a
        // resolved tenant (and must not open a connection to discover a bad SKU).
        var sku = Validate(input);

        using var db = contextFactory.Create();

        await EnsureSkuIsFreeAsync(db, sku, excludingProductId: null, ct);

        var now = DateTime.UtcNow;

        var product = new Product
        {
            Id = Guid.NewGuid(),
            // TenantId is deliberately left empty: TenantStampInterceptor stamps it from the
            // resolved tenant, so the client can never smuggle another tenant's id.
            Sku = sku,
            Name = input.Name.Trim(),
            Description = Normalize(input.Description),
            UnitPrice = input.UnitPrice,
            ReorderLevel = input.ReorderLevel,
            IsActive = input.IsActive,
            CreatedUtc = now,
            UpdatedUtc = now
        };

        db.Products.Add(product);
        db.StockLevels.Add(new StockLevel
        {
            Id = Guid.NewGuid(),
            ProductId = product.Id,
            Quantity = 0,
            UpdatedUtc = now
        });

        await db.SaveChangesAsync(ct);

        return product;
    }

    /// <inheritdoc />
    public async Task<Product> UpdateProductAsync(Guid id, ProductInput input, CancellationToken ct = default)
    {
        var sku = Validate(input);

        using var db = contextFactory.Create();

        var product = await db.Products.FirstOrDefaultAsync(p => p.Id == id, ct)
            ?? throw NotFound(id);

        await EnsureSkuIsFreeAsync(db, sku, excludingProductId: id, ct);

        product.Sku = sku;
        product.Name = input.Name.Trim();
        product.Description = Normalize(input.Description);
        product.UnitPrice = input.UnitPrice;
        product.ReorderLevel = input.ReorderLevel;
        product.IsActive = input.IsActive;
        product.UpdatedUtc = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);

        return product;
    }

    /// <inheritdoc />
    /// <remarks>
    /// A product with stock movements is refused rather than cascaded: the movement list is the
    /// audit trail for its stock, so silently deleting it (or leaving orphan history) would be a
    /// data-integrity decision nobody asked for. Deactivate it instead.
    /// </remarks>
    public async Task DeleteProductAsync(Guid id, CancellationToken ct = default)
    {
        using var db = contextFactory.Create();

        var product = await db.Products.FirstOrDefaultAsync(p => p.Id == id, ct)
            ?? throw NotFound(id);

        if (await db.StockMovements.AnyAsync(m => m.ProductId == id, ct))
        {
            throw new InvalidOperationException(
                $"Product '{product.Sku}' has stock movements and cannot be deleted; deactivate it so " +
                "its stock history stays intact.");
        }

        // No FK cascade exists in the schema, so the level row is cleaned up explicitly.
        var levels = await db.StockLevels.Where(s => s.ProductId == id).ToListAsync(ct);
        db.StockLevels.RemoveRange(levels);
        db.Products.Remove(product);

        await db.SaveChangesAsync(ct);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The movement row and the level update are committed by a single <c>SaveChangesAsync</c>, so
    /// they share one transaction: either both land or neither does.
    /// </remarks>
    public async Task<StockLevel> AdjustStockAsync(
        Guid productId,
        int delta,
        string reason,
        string? note,
        CancellationToken ct = default)
    {
        if (delta == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(delta), delta, "A stock adjustment must not be zero.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(reason.Length, ReasonMaxLength, nameof(reason));

        if (note is { Length: > DescriptionMaxLength })
        {
            throw new ArgumentException(
                $"A note may be at most {DescriptionMaxLength} characters.", nameof(note));
        }

        using var db = contextFactory.Create();

        _ = await db.Products.FirstOrDefaultAsync(p => p.Id == productId, ct)
            ?? throw NotFound(productId);

        var level = await db.StockLevels.FirstOrDefaultAsync(s => s.ProductId == productId, ct);

        if (level is null)
        {
            // Self-healing for a product that predates level tracking: start from zero.
            level = new StockLevel
            {
                Id = Guid.NewGuid(),
                ProductId = productId,
                Quantity = 0
            };

            db.StockLevels.Add(level);
        }

        var now = DateTime.UtcNow;

        level.Quantity += delta;
        level.UpdatedUtc = now;

        db.StockMovements.Add(new StockMovement
        {
            Id = Guid.NewGuid(),
            ProductId = productId,
            Delta = delta,
            Reason = reason.Trim(),
            Note = Normalize(note),
            OccurredUtc = now
        });

        await db.SaveChangesAsync(ct);

        return level;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<StockMovement>> ListMovementsAsync(
        Guid productId,
        CancellationToken ct = default)
    {
        using var db = contextFactory.Create();

        return await db.StockMovements
            .AsNoTracking()
            .Where(m => m.ProductId == productId)
            .OrderByDescending(m => m.OccurredUtc)
            .ToListAsync(ct);
    }

    private static KeyNotFoundException NotFound(Guid id) =>
        new($"No product with id '{id}' exists for the current tenant.");

    /// <summary>
    /// Validates a <see cref="ProductInput"/> and returns the trimmed SKU. Every message is meant to
    /// be shown to a user, so it says what to do rather than what went wrong internally.
    /// </summary>
    private static string Validate(ProductInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var sku = input.Sku?.Trim() ?? string.Empty;

        if (sku.Length == 0)
        {
            throw new ArgumentException("A SKU is required.", nameof(input));
        }

        if (!SkuRegex().IsMatch(sku))
        {
            throw new ArgumentException(
                "A SKU must be 2–32 characters using only A–Z, 0–9 and '-' (for example SKU-001).",
                nameof(input));
        }

        if (string.IsNullOrWhiteSpace(input.Name))
        {
            throw new ArgumentException("A product name is required.", nameof(input));
        }

        if (input.Name.Trim().Length > NameMaxLength)
        {
            throw new ArgumentException(
                $"A product name may be at most {NameMaxLength} characters.", nameof(input));
        }

        if (input.Description is { Length: > DescriptionMaxLength })
        {
            throw new ArgumentException(
                $"A description may be at most {DescriptionMaxLength} characters.", nameof(input));
        }

        if (input.UnitPrice < 0)
        {
            throw new ArgumentException("A unit price must be zero or greater.", nameof(input));
        }

        if (input.ReorderLevel < 0)
        {
            throw new ArgumentException("A reorder level must be zero or greater.", nameof(input));
        }

        return sku;
    }

    /// <summary>
    /// Friendly pre-check on top of the tenant-scoped unique index <c>ux_products_tenant_sku</c>:
    /// the index is the guarantee (and still catches a race), this is what turns the violation into
    /// a message rather than a <c>DbUpdateException</c>.
    /// </summary>
    private static async Task EnsureSkuIsFreeAsync(
        AppDbContext db,
        string sku,
        Guid? excludingProductId,
        CancellationToken ct)
    {
        var taken = await db.Products.AnyAsync(
            p => p.Sku == sku && (excludingProductId == null || p.Id != excludingProductId),
            ct);

        if (taken)
        {
            throw new ArgumentException(
                $"SKU '{sku}' is already used by another product in this tenant.", nameof(sku));
        }
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
