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
public class AuditController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly ITenantService _tenantService;

    public AuditController(AppDbContext context, ITenantService tenantService)
    {
        _context = context;
        _tenantService = tenantService;
    }

    [HttpPost("probe-isolation")]
    public async Task<IActionResult> ProbeCrossTenantIsolation([FromBody] ProbeIsolationRequest request)
    {
        var currentTenantId = _tenantService.CurrentTenantId;
        var currentTenantCode = _tenantService.CurrentTenantCode;

        // EF Core automatically applies the global query filter:
        // WHERE TenantId == CurrentTenantId
        // Any attempt by the current context to read items from targetTenantId will return 0 rows.
        var targetTenantGuid = Guid.TryParse(request.TargetTenantId, out var g) ? g : Guid.Empty;

        // Direct standard query through EF Core (which has query filters active)
        var visibleItems = await _context.InventoryItems
            .AsNoTracking()
            .Where(i => i.TenantId == targetTenantGuid)
            .ToListAsync();

        var sqlQuery = _context.InventoryItems
            .Where(i => i.TenantId == targetTenantGuid)
            .ToQueryString();

        var log = new TenantAuditLog
        {
            Action = "SECURITY_PENETRATION_PROBE",
            Details = $"Probe against tenant {request.TargetTenantId}. Result: {visibleItems.Count} rows leaked (Zero Leak Enforced).",
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "127.0.0.1",
            Timestamp = DateTime.UtcNow
        };
        _context.AuditLogs.Add(log);
        await _context.SaveChangesAsync();

        return Ok(new
        {
            success = true,
            isolated = visibleItems.Count == 0,
            activeTenantId = currentTenantId,
            activeTenantCode = currentTenantCode,
            targetTenantId = request.TargetTenantId,
            recordsLeaked = visibleItems.Count,
            sqlQuery,
            message = visibleItems.Count == 0 
                ? "EF Core Global Query Filter successfully blocked cross-tenant access. 0 records leaked." 
                : "ALERT: Tenant data leak detected!",
            status = visibleItems.Count == 0 ? "SECURE_ISOLATED" : "LEAK_VULNERABLE",
            timestamp = DateTime.UtcNow
        });
    }

    [HttpGet("logs")]
    public async Task<IActionResult> GetAuditLogs()
    {
        var logs = await _context.AuditLogs
            .AsNoTracking()
            .OrderByDescending(l => l.Timestamp)
            .Take(50)
            .ToListAsync();

        return Ok(logs);
    }
}

public record ProbeIsolationRequest(string TargetTenantId, string? ItemId);
