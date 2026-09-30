using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Acentra.MultiTenant.Core.Interfaces;
using Acentra.MultiTenant.Infrastructure.Data;

namespace Acentra.MultiTenant.Controllers;

[ApiController]
[Route("api/[controller]")]
public class StorageController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly ITenantService _tenantService;
    private readonly IS3StorageService _s3Storage;

    public StorageController(AppDbContext context, ITenantService tenantService, IS3StorageService s3Storage)
    {
        _context = context;
        _tenantService = tenantService;
        _s3Storage = s3Storage;
    }

    [HttpGet("files")]
    public async Task<IActionResult> GetTenantFiles()
    {
        var files = await _context.InventoryItems
            .AsNoTracking()
            .Where(i => i.S3FileKey != null)
            .Select(i => new
            {
                i.Id,
                i.Name,
                i.SKU,
                i.S3FileKey,
                i.FileUrl,
                i.UpdatedAt
            })
            .ToListAsync();

        return Ok(files);
    }

    [HttpGet("presigned-url")]
    public async Task<IActionResult> GetPresignedUrl([FromQuery] string fileKey)
    {
        if (string.IsNullOrWhiteSpace(fileKey))
        {
            return BadRequest(new { message = "File key is required." });
        }

        var tenantCode = _tenantService.CurrentTenantCode ?? "tenant";

        try
        {
            var url = await _s3Storage.GetPresignedDownloadUrlAsync(tenantCode, fileKey);
            return Ok(new { presignedUrl = url, expiresMinutes = 15 });
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(403, new { message = ex.Message });
        }
    }
}
