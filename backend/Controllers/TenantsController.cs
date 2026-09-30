using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Acentra.MultiTenant.Core.Entities;
using Acentra.MultiTenant.Core.Interfaces;
using Acentra.MultiTenant.Infrastructure.Data;

namespace Acentra.MultiTenant.Controllers;

[ApiController]
[Route("api/[controller]")]
public class TenantsController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly ITenantService _tenantService;

    public TenantsController(AppDbContext context, ITenantService tenantService)
    {
        _context = context;
        _tenantService = tenantService;
    }

    [HttpGet]
    public async Task<IActionResult> GetAllTenants()
    {
        var tenants = await _context.Tenants
            .AsNoTracking()
            .Where(t => t.IsActive)
            .OrderBy(t => t.Name)
            .ToListAsync();

        return Ok(tenants);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetTenantById(Guid id)
    {
        var tenant = await _context.Tenants.FindAsync(id);
        if (tenant == null) return NotFound(new { message = "Tenant not found" });
        return Ok(tenant);
    }

    [HttpPost]
    public async Task<IActionResult> CreateTenant([FromBody] CreateTenantRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.Code))
        {
            return BadRequest(new { message = "Tenant name and code are required." });
        }

        var normalizedCode = request.Code.ToLowerInvariant().Trim();
        if (await _context.Tenants.AnyAsync(t => t.Code == normalizedCode))
        {
            return Conflict(new { message = $"Tenant code '{normalizedCode}' already exists." });
        }

        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Name = request.Name.Trim(),
            Code = normalizedCode,
            SubscriptionTier = request.SubscriptionTier ?? "Enterprise",
            S3BucketPrefix = normalizedCode,
            CreatedAt = DateTime.UtcNow
        };

        _context.Tenants.Add(tenant);
        await _context.SaveChangesAsync();

        return CreatedAtAction(nameof(GetTenantById), new { id = tenant.Id }, tenant);
    }

    [HttpGet("current/metrics")]
    public async Task<IActionResult> GetCurrentTenantMetrics()
    {
        if (_tenantService.CurrentTenantId == null)
        {
            return BadRequest(new { message = "No active tenant header specified (X-Tenant-ID)." });
        }

        var totalSkus = await _context.InventoryItems.CountAsync();
        var totalValuation = await _context.InventoryItems.SumAsync(i => (decimal?)i.Quantity * i.UnitPrice) ?? 0;
        var lowStockCount = await _context.InventoryItems.CountAsync(i => i.Quantity <= i.ReorderLevel);
        var s3FilesCount = await _context.InventoryItems.CountAsync(i => i.S3FileKey != null);

        return Ok(new
        {
            tenantId = _tenantService.CurrentTenantId,
            tenantCode = _tenantService.CurrentTenantCode,
            totalSkus,
            totalValuation,
            lowStockCount,
            s3FilesCount
        });
    }
}

public record CreateTenantRequest(string Name, string Code, string? SubscriptionTier);
