using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Acentra.MultiTenant.Core.Entities;

namespace Acentra.MultiTenant.Infrastructure.Data;

public static class DbSeeder
{
    public static async Task SeedAsync(AppDbContext context)
    {
        if (await context.Tenants.AnyAsync()) return;

        var tenant1 = new Tenant
        {
            Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Code = "apex-health",
            Name = "Apex Healthcare System",
            SubscriptionTier = "Enterprise",
            S3BucketPrefix = "apex-health"
        };

        var tenant2 = new Tenant
        {
            Id = Guid.Parse("22222222-2222-2222-2222-222222222222"),
            Code = "biomed-labs",
            Name = "BioMed Diagnostics & Labs",
            SubscriptionTier = "Professional",
            S3BucketPrefix = "biomed-labs"
        };

        var tenant3 = new Tenant
        {
            Id = Guid.Parse("33333333-3333-3333-3333-333333333333"),
            Code = "novacare-pharma",
            Name = "NovaCare Pharmaceuticals",
            SubscriptionTier = "Enterprise",
            S3BucketPrefix = "novacare-pharma"
        };

        await context.Tenants.AddRangeAsync(tenant1, tenant2, tenant3);

        // Seed Inventory for Tenant 1 (Apex Healthcare)
        var itemsTenant1 = new List<InventoryItem>
        {
            new() { Id = Guid.NewGuid(), TenantId = tenant1.Id, SKU = "APX-AMX-500", Name = "Amoxicillin 500mg USP Capsules", Category = "Pharmaceuticals", Quantity = 240, UnitPrice = 18.50m, ReorderLevel = 50, Description = "Broad-spectrum antibiotic for bacterial infections", S3FileKey = "apex-health/docs/APX-AMX-500/amox_spec_sheet.pdf" },
            new() { Id = Guid.NewGuid(), TenantId = tenant1.Id, SKU = "APX-N95-SURG", Name = "N95 Surgical Respirator Masks (Box of 50)", Category = "PPE & Safety", Quantity = 18, UnitPrice = 35.00m, ReorderLevel = 30, Description = "NIOSH approved particulate respirator" },
            new() { Id = Guid.NewGuid(), TenantId = tenant1.Id, SKU = "APX-SCALP-10", Name = "Sterile Disposable Scalpel #10 (Box of 20)", Category = "Surgical Equipment", Quantity = 150, UnitPrice = 42.00m, ReorderLevel = 25, Description = "Precision surgical blade with safety guard", S3FileKey = "apex-health/docs/APX-SCALP-10/scalpel_cert.pdf" },
            new() { Id = Guid.NewGuid(), TenantId = tenant1.Id, SKU = "APX-IV-SALINE", Name = "0.9% Sodium Chloride IV Saline 1000ml", Category = "Consumables", Quantity = 500, UnitPrice = 8.75m, ReorderLevel = 100, Description = "Sterile isotonic IV infusion fluid" },
        };

        // Seed Inventory for Tenant 2 (BioMed Labs)
        var itemsTenant2 = new List<InventoryItem>
        {
            new() { Id = Guid.NewGuid(), TenantId = tenant2.Id, SKU = "BIO-PCR-COVID", Name = "RT-PCR Multiplex Viral Detection Assay Kit", Category = "Diagnostics", Quantity = 85, UnitPrice = 145.00m, ReorderLevel = 20, Description = "High sensitivity viral RNA test kit for clinical labs", S3FileKey = "biomed-labs/docs/BIO-PCR-COVID/fda_eua_cert.pdf" },
            new() { Id = Guid.NewGuid(), TenantId = tenant2.Id, SKU = "BIO-CENT-TUBE", Name = "Conical Centrifuge Tubes 50mL (Pack of 500)", Category = "Consumables", Quantity = 12, UnitPrice = 65.00m, ReorderLevel = 15, Description = "Graduated sterile polypropylene tubes with screw caps" },
            new() { Id = Guid.NewGuid(), TenantId = tenant2.Id, SKU = "BIO-HEPA-FLTR", Name = "Micro-Biological Grade HEPA Filter Core", Category = "PPE & Safety", Quantity = 8, UnitPrice = 280.00m, ReorderLevel = 10, Description = "99.97% particulate retention filter", S3FileKey = "biomed-labs/docs/BIO-HEPA-FLTR/filter_specs.pdf" },
        };

        // Seed Inventory for Tenant 3 (NovaCare Pharma)
        var itemsTenant3 = new List<InventoryItem>
        {
            new() { Id = Guid.NewGuid(), TenantId = tenant3.Id, SKU = "NOVA-INS-GLARG", Name = "Insulin Glargine 100 Units/mL SoloStar", Category = "Pharmaceuticals", Quantity = 420, UnitPrice = 88.00m, ReorderLevel = 60, Description = "Long-acting basal human insulin analog", S3FileKey = "novacare-pharma/docs/NOVA-INS-GLARG/pharma_monograph.pdf" },
            new() { Id = Guid.NewGuid(), TenantId = tenant3.Id, SKU = "NOVA-ATV-20MG", Name = "Atorvastatin Calcium 20mg Tablets", Category = "Pharmaceuticals", Quantity = 950, UnitPrice = 12.20m, ReorderLevel = 150, Description = "Lipid-lowering HMG-CoA reductase inhibitor" },
        };

        await context.InventoryItems.AddRangeAsync(itemsTenant1);
        await context.InventoryItems.AddRangeAsync(itemsTenant2);
        await context.InventoryItems.AddRangeAsync(itemsTenant3);

        await context.SaveChangesAsync();
    }
}
