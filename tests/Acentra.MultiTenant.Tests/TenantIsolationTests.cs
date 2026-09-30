using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Configuration;
using Xunit;
using Acentra.MultiTenant.Core.Entities;
using Acentra.MultiTenant.Core.Interfaces;
using Acentra.MultiTenant.Infrastructure.Data;
using Acentra.MultiTenant.Infrastructure.Services;

namespace Acentra.MultiTenant.Tests;

public class TenantIsolationTests
{
    [Fact]
    public async Task EFCore_GlobalQueryFilter_StrictlyHidesOtherTenantData()
    {
        var tenant1Id = Guid.NewGuid();
        var tenant2Id = Guid.NewGuid();

        var dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        var tenantService = new TenantService();

        // 1. Seed items under Tenant 1 & Tenant 2 using unscoped context
        using (var seedContext = new AppDbContext(dbOptions, null))
        {
            seedContext.InventoryItems.Add(new InventoryItem
            {
                Id = Guid.NewGuid(),
                TenantId = tenant1Id,
                SKU = "T1-ITEM",
                Name = "Apex Ventilator Core",
                Quantity = 50,
                UnitPrice = 500m
            });

            seedContext.InventoryItems.Add(new InventoryItem
            {
                Id = Guid.NewGuid(),
                TenantId = tenant2Id,
                SKU = "T2-ITEM",
                Name = "BioMed Centrifuge",
                Quantity = 10,
                UnitPrice = 1200m
            });

            await seedContext.SaveChangesAsync();
        }

        // 2. Query as Tenant 1
        tenantService.SetTenantId(tenant1Id, "apex-health");
        using (var tenant1Context = new AppDbContext(dbOptions, tenantService))
        {
            var tenant1Items = await tenant1Context.InventoryItems.ToListAsync();
            
            // Assertions
            Assert.Single(tenant1Items);
            Assert.Equal("T1-ITEM", tenant1Items[0].SKU);
            Assert.Equal(tenant1Id, tenant1Items[0].TenantId);

            // Attempting direct query for Tenant 2's item should return NULL
            var crossQuery = await tenant1Context.InventoryItems.FirstOrDefaultAsync(i => i.TenantId == tenant2Id);
            Assert.Null(crossQuery);
        }

        // 3. Query as Tenant 2
        tenantService.SetTenantId(tenant2Id, "biomed-labs");
        using (var tenant2Context = new AppDbContext(dbOptions, tenantService))
        {
            var tenant2Items = await tenant2Context.InventoryItems.ToListAsync();
            
            // Assertions
            Assert.Single(tenant2Items);
            Assert.Equal("T2-ITEM", tenant2Items[0].SKU);
            Assert.Equal(tenant2Id, tenant2Items[0].TenantId);
        }
    }

    [Fact]
    public async Task EFCore_AutomaticTenantId_InjectedOnSave()
    {
        var tenantId = Guid.NewGuid();
        var dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        var tenantService = new TenantService();
        tenantService.SetTenantId(tenantId, "test-tenant");

        using var context = new AppDbContext(dbOptions, tenantService);
        var item = new InventoryItem
        {
            Id = Guid.NewGuid(),
            SKU = "AUTO-TENANT-SKU",
            Name = "Automated Syringe Pump",
            Quantity = 100,
            UnitPrice = 45m
            // Notice: TenantId is NOT explicitly set!
        };

        context.InventoryItems.Add(item);
        await context.SaveChangesAsync();

        // Assert that EF Core interceptor automatically injected the active tenant ID
        Assert.Equal(tenantId, item.TenantId);
    }

    [Fact]
    public async Task S3StorageService_Enforces_TenantPrefixIsolation()
    {
        var inMemoryConfig = new ConfigurationBuilder().Build();
        var logger = NullLogger<S3StorageService>.Instance;
        var s3Service = new S3StorageService(inMemoryConfig, logger);

        // Valid access within tenant partition
        var validUrl = await s3Service.GetPresignedDownloadUrlAsync("apex-health", "apex-health/docs/report.pdf");
        Assert.Contains("apex-health/docs/report.pdf", validUrl);

        // Malicious cross-tenant attempt should throw UnauthorizedAccessException
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
        {
            await s3Service.GetPresignedDownloadUrlAsync("apex-health", "victim-tenant/docs/confidential.pdf");
        });
    }
}
