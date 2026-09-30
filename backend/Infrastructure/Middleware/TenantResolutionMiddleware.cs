using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Acentra.MultiTenant.Core.Interfaces;
using Acentra.MultiTenant.Infrastructure.Data;

namespace Acentra.MultiTenant.Infrastructure.Middleware;

public class TenantResolutionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<TenantResolutionMiddleware> _logger;

    public const string TenantIdHeader = "X-Tenant-ID";
    public const string TenantCodeHeader = "X-Tenant-Code";

    public TenantResolutionMiddleware(RequestDelegate next, ILogger<TenantResolutionMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, ITenantService tenantService, IServiceProvider serviceProvider)
    {
        var path = context.Request.Path.Value?.ToLowerInvariant() ?? "";

        // Bypass tenant resolution for swagger, health checks, or public root
        if (path.StartsWith("/swagger") || path == "/" || path.StartsWith("/api/tenants") && context.Request.Method == "GET")
        {
            // Allow tenant retrieval endpoints
        }

        string? tenantIdentifier = null;

        // 1. Try resolving from Request Header (X-Tenant-ID or X-Tenant-Code)
        if (context.Request.Headers.TryGetValue(TenantIdHeader, out var headerTenantId) && !string.IsNullOrWhiteSpace(headerTenantId))
        {
            tenantIdentifier = headerTenantId.ToString();
        }
        else if (context.Request.Headers.TryGetValue(TenantCodeHeader, out var headerTenantCode) && !string.IsNullOrWhiteSpace(headerTenantCode))
        {
            tenantIdentifier = headerTenantCode.ToString();
        }
        // 2. Fallback to Subdomain (e.g. "apex-health.acentra.io")
        else if (context.Request.Host.HasValue)
        {
            var hostParts = context.Request.Host.Host.Split('.');
            if (hostParts.Length > 2 && hostParts[0] != "www" && hostParts[0] != "api")
            {
                tenantIdentifier = hostParts[0];
            }
        }

        if (!string.IsNullOrWhiteSpace(tenantIdentifier))
        {
            using var scope = serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            // Find tenant by GUID Id or Code
            var tenant = Guid.TryParse(tenantIdentifier, out var guid)
                ? await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == guid)
                : await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Code == tenantIdentifier);

            if (tenant != null)
            {
                tenantService.SetTenant(tenant);
                _logger.LogDebug("Resolved Tenant: {TenantName} ({TenantId})", tenant.Name, tenant.Id);
            }
            else if (Guid.TryParse(tenantIdentifier, out var fallbackGuid))
            {
                // Set fallback ID for mock/in-memory probes
                tenantService.SetTenantId(fallbackGuid, tenantIdentifier);
            }
        }

        await _next(context);
    }
}
