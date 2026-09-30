using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Acentra.MultiTenant.Core.Entities;
using Acentra.MultiTenant.Core.Interfaces;
using Acentra.MultiTenant.Infrastructure.Data;

namespace Acentra.MultiTenant.Controllers;

[ApiController]
[Route("api/[controller]")]
public class InventoryController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly ITenantService _tenantService;
    private readonly IS3StorageService _s3Storage;

    public InventoryController(AppDbContext context, ITenantService tenantService, IS3StorageService s3Storage)
    {
        _context = context;
        _tenantService = tenantService;
        _s3Storage = s3Storage;
    }

    [HttpGet]
    public async Task<IActionResult> GetInventory([FromQuery] string? category, [FromQuery] string? search)
    {
        // Global Query Filter automatically applies: WHERE TenantId == CurrentTenantId
        var query = _context.InventoryItems.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(category) && category != "ALL")
        {
            query = query.Where(i => i.Category == category);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.ToLowerInvariant();
            query = query.Where(i => i.Name.ToLower().Contains(s) || i.SKU.ToLower().Contains(s));
        }

        var items = await query.OrderBy(i => i.Name).ToListAsync();
        return Ok(items);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id)
    {
        // Query filter prevents accessing another tenant's item
        var item = await _context.InventoryItems.FirstOrDefaultAsync(i => i.Id == id);
        if (item == null) return NotFound(new { message = "Item not found in current tenant partition." });
        return Ok(item);
    }

    [HttpPost]
    public async Task<IActionResult> CreateItem([FromBody] CreateInventoryItemRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.SKU) || string.IsNullOrWhiteSpace(request.Name))
        {
            return BadRequest(new { message = "SKU and Name are mandatory." });
        }

        var item = new InventoryItem
        {
            Id = Guid.NewGuid(),
            SKU = request.SKU.ToUpperInvariant().Trim(),
            Name = request.Name.Trim(),
            Category = request.Category ?? "General",
            Quantity = request.Quantity,
            UnitPrice = request.UnitPrice,
            ReorderLevel = request.ReorderLevel > 0 ? request.ReorderLevel : 20,
            Description = request.Description,
            CreatedAt = DateTime.UtcNow
        };

        // AppDbContext SaveChanges automatically sets item.TenantId = _tenantService.CurrentTenantId
        _context.InventoryItems.Add(item);
        await _context.SaveChangesAsync();

        return CreatedAtAction(nameof(GetById), new { id = item.Id }, item);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> UpdateItem(Guid id, [FromBody] UpdateInventoryItemRequest request)
    {
        var item = await _context.InventoryItems.FirstOrDefaultAsync(i => i.Id == id);
        if (item == null) return NotFound(new { message = "Item not found in current tenant partition." });

        item.Name = request.Name ?? item.Name;
        item.Category = request.Category ?? item.Category;
        item.UnitPrice = request.UnitPrice ?? item.UnitPrice;
        item.ReorderLevel = request.ReorderLevel ?? item.ReorderLevel;
        item.Description = request.Description ?? item.Description;
        item.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();
        return Ok(item);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> DeleteItem(Guid id)
    {
        var item = await _context.InventoryItems.FirstOrDefaultAsync(i => i.Id == id);
        if (item == null) return NotFound(new { message = "Item not found in current tenant partition." });

        _context.InventoryItems.Remove(item);
        await _context.SaveChangesAsync();
        return NoContent();
    }

    [HttpPost("{id:guid}/adjust-stock")]
    public async Task<IActionResult> AdjustStock(Guid id, [FromBody] StockAdjustmentRequest request)
    {
        var item = await _context.InventoryItems.FirstOrDefaultAsync(i => i.Id == id);
        if (item == null) return NotFound(new { message = "Item not found in current tenant partition." });

        int previousStock = item.Quantity;
        int qtyDelta = 0;

        if (request.Type.Equals("INBOUND", StringComparison.OrdinalIgnoreCase))
        {
            qtyDelta = request.Quantity;
            item.Quantity += request.Quantity;
        }
        else if (request.Type.Equals("OUTBOUND", StringComparison.OrdinalIgnoreCase))
        {
            qtyDelta = -request.Quantity;
            item.Quantity = Math.Max(0, item.Quantity - request.Quantity);
        }
        else if (request.Type.Equals("AUDIT_CORRECTION", StringComparison.OrdinalIgnoreCase))
        {
            qtyDelta = request.Quantity - item.Quantity;
            item.Quantity = request.Quantity;
        }

        var transaction = new StockTransaction
        {
            InventoryItemId = item.Id,
            ItemSku = item.SKU,
            ItemName = item.Name,
            TransactionType = request.Type.ToUpperInvariant(),
            QuantityChanged = qtyDelta,
            ResultingStock = item.Quantity,
            Reason = request.Reason ?? "Manual stock adjustment",
            Timestamp = DateTime.UtcNow
        };

        _context.StockTransactions.Add(transaction);
        await _context.SaveChangesAsync();

        return Ok(new { item, transaction });
    }

    [HttpPost("{id:guid}/upload-s3")]
    public async Task<IActionResult> UploadS3Document(Guid id, IFormFile file)
    {
        if (file == null || file.Length == 0)
        {
            return BadRequest(new { message = "No file provided." });
        }

        var item = await _context.InventoryItems.FirstOrDefaultAsync(i => i.Id == id);
        if (item == null) return NotFound(new { message = "Item not found in current tenant partition." });

        var tenantCode = _tenantService.CurrentTenantCode ?? "tenant";

        using var stream = file.OpenReadStream();
        var s3Key = await _s3Storage.UploadFileAsync(tenantCode, "docs", file.FileName, stream, file.ContentType);
        var presignedUrl = await _s3Storage.GetPresignedDownloadUrlAsync(tenantCode, s3Key);

        item.S3FileKey = s3Key;
        item.FileUrl = presignedUrl;
        item.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();
        return Ok(new { message = "File uploaded to tenant S3 partition successfully.", s3Key, presignedUrl });
    }
}

public record CreateInventoryItemRequest(string SKU, string Name, string? Category, int Quantity, decimal UnitPrice, int ReorderLevel, string? Description);
public record UpdateInventoryItemRequest(string? Name, string? Category, decimal? UnitPrice, int? ReorderLevel, string? Description);
public record StockAdjustmentRequest(string Type, int Quantity, string? Reason);
