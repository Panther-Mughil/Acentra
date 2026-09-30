using System.Text;
using Acentra.Web.Models;

namespace Acentra.Web.Services;

#region Toast Service

public enum ToastLevel
{
    Info,
    Success,
    Warning,
    Error
}

public class ToastMessage
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Title { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public ToastLevel Level { get; set; } = ToastLevel.Info;
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public int DurationMs { get; set; } = 4000;
}

public class ToastService
{
    public event Action<ToastMessage>? OnToastAdded;
    public event Action<string>? OnToastRemoved;

    private readonly List<ToastMessage> _messages = new();
    public IReadOnlyList<ToastMessage> Messages => _messages.AsReadOnly();

    public void ShowToast(string title, string message, ToastLevel level = ToastLevel.Info, int durationMs = 4000)
    {
        var toast = new ToastMessage
        {
            Title = title,
            Message = message,
            Level = level,
            DurationMs = durationMs
        };
        _messages.Add(toast);
        OnToastAdded?.Invoke(toast);
    }

    public void Success(string title, string message) => ShowToast(title, message, ToastLevel.Success);
    public void Warning(string title, string message) => ShowToast(title, message, ToastLevel.Warning);
    public void Error(string title, string message) => ShowToast(title, message, ToastLevel.Error);
    public void Info(string title, string message) => ShowToast(title, message, ToastLevel.Info);

    public void RemoveToast(string id)
    {
        var item = _messages.FirstOrDefault(x => x.Id == id);
        if (item != null)
        {
            _messages.Remove(item);
            OnToastRemoved?.Invoke(id);
        }
    }
}

#endregion

#region Tenant State Service

public class TenantStateService
{
    private readonly ToastService _toastService;

    public event Action? OnTenantChanged;

    public List<Tenant> AvailableTenants { get; private set; } = new();
    public Tenant CurrentTenant { get; private set; }
    public Branch CurrentBranch { get; private set; }

    public TenantStateService(ToastService toastService)
    {
        _toastService = toastService;
        InitializeTenants();
        CurrentTenant = AvailableTenants[0];
        CurrentBranch = CurrentTenant.Branches[0];
    }

    private void InitializeTenants()
    {
        AvailableTenants = new List<Tenant>
        {
            new Tenant
            {
                Id = "TEN-001",
                Name = "Apollo Healthcare",
                Code = "APOLLO-HC",
                Region = "Chennai & South India",
                BranchCount = 8,
                ActiveUsersCount = 1420,
                TotalStockValue = 48250000m,
                EncryptionKeyId = "KMS-APOLLO-98124-AES256",
                PrimaryContact = "Dr. S. Ramanathan (Chief Operations)",
                Branches = new List<Branch>
                {
                    new Branch { Id = "BR-01", Name = "Chennai • Central Hospital", Code = "CHE-MAIN", City = "Chennai", Address = "Greams Road, Thousand Lights", TotalItemsCount = 12840, LowStockCount = 27 },
                    new Branch { Id = "BR-02", Name = "Chennai • OMR Specialty Center", Code = "CHE-OMR", City = "Chennai", Address = "IT Corridor, Sholinganallur", TotalItemsCount = 8450, LowStockCount = 14 },
                    new Branch { Id = "BR-03", Name = "Bangalore • Bannerghatta Main", Code = "BLR-BNG", City = "Bangalore", Address = "Opp IIMB, Bannerghatta Rd", TotalItemsCount = 11200, LowStockCount = 19 },
                    new Branch { Id = "BR-04", Name = "Hyderabad • Jubilee Hills", Code = "HYD-JUB", City = "Hyderabad", Address = "Road No 72, Jubilee Hills", TotalItemsCount = 9800, LowStockCount = 12 }
                }
            },
            new Tenant
            {
                Id = "TEN-002",
                Name = "MedCare Network",
                Code = "MEDCARE-NET",
                Region = "Tamil Nadu",
                BranchCount = 12,
                ActiveUsersCount = 980,
                TotalStockValue = 32400000m,
                EncryptionKeyId = "KMS-MEDCARE-44102-AES256",
                PrimaryContact = "K. Ananya (Logistics Director)",
                Branches = new List<Branch>
                {
                    new Branch { Id = "BR-10", Name = "Coimbatore • City Hospital", Code = "CBE-CITY", City = "Coimbatore", Address = "Avinashi Road, Peelamedu", TotalItemsCount = 7600, LowStockCount = 18 },
                    new Branch { Id = "BR-11", Name = "Madurai • Super Specialty", Code = "MDU-SPC", City = "Madurai", Address = "KK Nagar Bypass", TotalItemsCount = 6900, LowStockCount = 15 }
                }
            },
            new Tenant
            {
                Id = "TEN-003",
                Name = "Nova Hospitals",
                Code = "NOVA-HOSP",
                Region = "South India",
                BranchCount = 6,
                ActiveUsersCount = 640,
                TotalStockValue = 21900000m,
                EncryptionKeyId = "KMS-NOVA-88219-AES256",
                PrimaryContact = "Vikram Sen (VP Procurement)",
                Branches = new List<Branch>
                {
                    new Branch { Id = "BR-20", Name = "Kochi • Marine Drive Center", Code = "COK-MD", City = "Kochi", Address = "Marine Drive, Ernakulam", TotalItemsCount = 5400, LowStockCount = 9 },
                    new Branch { Id = "BR-21", Name = "Trivandrum • Medical Enclave", Code = "TRV-MED", City = "Trivandrum", Address = "Pattom Junction", TotalItemsCount = 4900, LowStockCount = 8 }
                }
            }
        };
    }

    public void SwitchTenant(string tenantId, string? branchId = null)
    {
        var tenant = AvailableTenants.FirstOrDefault(t => t.Id == tenantId);
        if (tenant != null)
        {
            CurrentTenant = tenant;
            CurrentBranch = (branchId != null ? tenant.Branches.FirstOrDefault(b => b.Id == branchId) : null)
                            ?? tenant.Branches.FirstOrDefault()
                            ?? new Branch { Name = "Main Branch", Code = "MAIN" };

            _toastService.Success(
                "Organization Context Switched",
                $"You are now viewing: {CurrentTenant.Name} ({CurrentBranch.Name}) with isolated AES-256 context."
            );

            OnTenantChanged?.Invoke();
        }
    }

    public void SwitchBranch(string branchId)
    {
        var branch = CurrentTenant.Branches.FirstOrDefault(b => b.Id == branchId);
        if (branch != null)
        {
            CurrentBranch = branch;
            _toastService.Info("Branch Context Updated", $"Active branch changed to {CurrentBranch.Name}.");
            OnTenantChanged?.Invoke();
        }
    }

    public void AddOrganization(string name, string code, string region, string branchName, string city)
    {
        var newId = $"TEN-{AvailableTenants.Count + 1:D3}";
        var newBranchId = $"BR-{newId}-01";
        var newTenant = new Tenant
        {
            Id = newId,
            Name = name,
            Code = code.ToUpperInvariant(),
            Region = region,
            BranchCount = 1,
            ActiveUsersCount = 1,
            TotalStockValue = 1000000m,
            EncryptionKeyId = $"KMS-{code.ToUpperInvariant()}-{Random.Shared.Next(10000, 99999)}-AES256",
            PrimaryContact = "Admin User",
            Branches = new List<Branch>
            {
                new Branch
                {
                    Id = newBranchId,
                    Name = branchName,
                    Code = $"{code.Substring(0, Math.Min(3, code.Length)).ToUpper()}-01",
                    City = city,
                    Address = "Primary Medical Campus",
                    TotalItemsCount = 120,
                    LowStockCount = 2
                }
            }
        };

        AvailableTenants.Add(newTenant);
        SwitchTenant(newTenant.Id);
    }
}

#endregion

#region Inventory Mock Service

public class InventoryService
{
    private readonly List<InventoryItem> _items = new();
    private readonly List<StockMovement> _movements = new();
    private readonly ToastService _toastService;

    public InventoryService(ToastService toastService)
    {
        _toastService = toastService;
        SeedInventory();
    }

    private void SeedInventory()
    {
        _items.Clear();
        _items.AddRange(new List<InventoryItem>
        {
            new InventoryItem
            {
                Id = "ITM-001",
                Sku = "MED-2048",
                Name = "Surgical Nitrile Gloves (Latex-Free, M)",
                Category = "PPE & Safety",
                BranchId = "BR-01",
                BranchName = "Chennai • Central Hospital",
                Unit = "Boxes (100 pcs)",
                CurrentStock = 1240,
                ReorderLevel = 2000,
                OptimalStock = 5000,
                UnitCost = 450m,
                UnitPrice = 650m,
                SupplierId = "SUP-01",
                SupplierName = "HealthSupply India Pvt Ltd",
                Status = StockStatus.LowStock,
                StorageLocation = "Warehouse Zone B - Bay 04",
                BatchNumber = "BATCH-2026-09A",
                ExpiryDate = DateTime.Now.AddMonths(18),
                LastUpdated = DateTime.Now.AddMinutes(-2),
                Description = "Hospital grade, powder-free textured nitrile surgical gloves. Tested for chemotherapy drugs.",
                TemperatureRequirement = "Dry & Cool (15°C - 28°C)"
            },
            new InventoryItem
            {
                Id = "ITM-002",
                Sku = "MED-8810",
                Name = "Propofol Injectable Emulsion 10mg/mL",
                Category = "Pharmaceuticals",
                BranchId = "BR-01",
                BranchName = "Chennai • Central Hospital",
                Unit = "Vials (20mL)",
                CurrentStock = 340,
                ReorderLevel = 500,
                OptimalStock = 1200,
                UnitCost = 820m,
                UnitPrice = 1150m,
                SupplierId = "SUP-02",
                SupplierName = "Apex Pharma Logistics",
                Status = StockStatus.LowStock,
                StorageLocation = "Cold Chain Vault 02 - Shelf 1",
                BatchNumber = "APX-88219-EXP",
                ExpiryDate = DateTime.Now.AddMonths(8),
                LastUpdated = DateTime.Now.AddMinutes(-15),
                Description = "General anesthetic intravenous sedative. Strict temperature-controlled cold chain required.",
                TemperatureRequirement = "Cold Chain (2°C - 8°C)"
            },
            new InventoryItem
            {
                Id = "ITM-003",
                Sku = "SUR-1044",
                Name = "Titanium Bone Screws 3.5mm x 24mm",
                Category = "Surgical Equipment",
                BranchId = "BR-01",
                BranchName = "Chennai • Central Hospital",
                Unit = "Packs (5 units)",
                CurrentStock = 48,
                ReorderLevel = 50,
                OptimalStock = 150,
                UnitCost = 3200m,
                UnitPrice = 4800m,
                SupplierId = "SUP-03",
                SupplierName = "MedTech Precision Devices",
                Status = StockStatus.Critical,
                StorageLocation = "Orthopedic OT Vault - Bin 12",
                BatchNumber = "MT-TITAN-2026-X",
                ExpiryDate = DateTime.Now.AddYears(4),
                LastUpdated = DateTime.Now.AddHours(-1),
                Description = "Biocompatible surgical titanium cortical bone fixators. Gamma sterilized with RFID tags.",
                TemperatureRequirement = "Sterile Storage"
            },
            new InventoryItem
            {
                Id = "ITM-004",
                Sku = "PPE-9012",
                Name = "N95 Particulate Respirator Masks (Fluid Resistant)",
                Category = "PPE & Safety",
                BranchId = "BR-01",
                BranchName = "Chennai • Central Hospital",
                Unit = "Boxes (50 pcs)",
                CurrentStock = 3850,
                ReorderLevel = 1500,
                OptimalStock = 4000,
                UnitCost = 950m,
                UnitPrice = 1350m,
                SupplierId = "SUP-01",
                SupplierName = "HealthSupply India Pvt Ltd",
                Status = StockStatus.Healthy,
                StorageLocation = "Main Floor Supply Room 1",
                BatchNumber = "3M-N95-2026-7A",
                ExpiryDate = DateTime.Now.AddYears(3),
                LastUpdated = DateTime.Now.AddHours(-3),
                Description = "NIOSH-approved N95 particulate respirator mask with 3-layer nanofiber filtration barrier.",
                TemperatureRequirement = "Ambient (10°C - 30°C)"
            },
            new InventoryItem
            {
                Id = "ITM-005",
                Sku = "IV-5510",
                Name = "Normal Saline 0.9% Sodium Chloride 500mL",
                Category = "IV Fluids & Infusions",
                BranchId = "BR-01",
                BranchName = "Chennai • Central Hospital",
                Unit = "Bags (24/Carton)",
                CurrentStock = 1840,
                ReorderLevel = 800,
                OptimalStock = 2000,
                UnitCost = 380m,
                UnitPrice = 540m,
                SupplierId = "SUP-04",
                SupplierName = "National Parenteral Corp",
                Status = StockStatus.Healthy,
                StorageLocation = "Central Pharmacy Rack 09",
                BatchNumber = "NS-09-8812B",
                ExpiryDate = DateTime.Now.AddMonths(24),
                LastUpdated = DateTime.Now.AddHours(-5),
                Description = "Sterile isotonic intravenous solution for volume replenishment and medication dilution.",
                TemperatureRequirement = "Controlled Room Temp (20°C - 25°C)"
            },
            new InventoryItem
            {
                Id = "ITM-006",
                Sku = "LAB-3011",
                Name = "Troponin-I Rapid Diagnostic Test Cartridges",
                Category = "Laboratory & Diagnostics",
                BranchId = "BR-01",
                BranchName = "Chennai • Central Hospital",
                Unit = "Kits (25 tests)",
                CurrentStock = 115,
                ReorderLevel = 60,
                OptimalStock = 120,
                UnitCost = 4500m,
                UnitPrice = 6800m,
                SupplierId = "SUP-05",
                SupplierName = "Bioscan Diagnostic Systems",
                Status = StockStatus.Healthy,
                StorageLocation = "Emergency Lab Refrigerator 3",
                BatchNumber = "BS-TROP-902",
                ExpiryDate = DateTime.Now.AddMonths(11),
                LastUpdated = DateTime.Now.AddHours(-6),
                Description = "Point-of-care cardiac biomarker quantitative immunoassay cassette for acute myocardial infarction.",
                TemperatureRequirement = "Refrigerated (2°C - 8°C)"
            },
            new InventoryItem
            {
                Id = "ITM-007",
                Sku = "DIA-4490",
                Name = "Hemodialysis Dialyzer High-Flux Filters",
                Category = "Medical Supplies",
                BranchId = "BR-01",
                BranchName = "Chennai • Central Hospital",
                Unit = "Units",
                CurrentStock = 880,
                ReorderLevel = 400,
                OptimalStock = 700,
                UnitCost = 1450m,
                UnitPrice = 2100m,
                SupplierId = "SUP-03",
                SupplierName = "MedTech Precision Devices",
                Status = StockStatus.Overstock,
                StorageLocation = "Dialysis Center Store Room",
                BatchNumber = "HF-DIAL-771",
                ExpiryDate = DateTime.Now.AddYears(2),
                LastUpdated = DateTime.Now.AddDays(-1),
                Description = "Polysulfone membrane synthetic high-flux dialyzer with superior clearance of middle molecules.",
                TemperatureRequirement = "Ambient (15°C - 30°C)"
            },
            new InventoryItem
            {
                Id = "ITM-008",
                Sku = "SUR-7712",
                Name = "Surgical Monofilament Suture 3-0 Vicryl",
                Category = "Surgical Equipment",
                BranchId = "BR-01",
                BranchName = "Chennai • Central Hospital",
                Unit = "Boxes (36 pcs)",
                CurrentStock = 24,
                ReorderLevel = 80,
                OptimalStock = 200,
                UnitCost = 2100m,
                UnitPrice = 3100m,
                SupplierId = "SUP-03",
                SupplierName = "MedTech Precision Devices",
                Status = StockStatus.Critical,
                StorageLocation = "OT Central Sterile Station 2",
                BatchNumber = "ETH-VIC-441",
                ExpiryDate = DateTime.Now.AddYears(3),
                LastUpdated = DateTime.Now.AddMinutes(-40),
                Description = "Absorbable synthetic braided suture with reverse cutting precision needle.",
                TemperatureRequirement = "Moisture-proof Ambient"
            }
        });

        // Seed stock history timeline
        _movements.Clear();
        _movements.AddRange(new List<StockMovement>
        {
            new StockMovement
            {
                Id = "MOV-101",
                ItemId = "ITM-001",
                ItemName = "Surgical Nitrile Gloves (Latex-Free, M)",
                MovementType = "Consumed",
                Quantity = -260,
                PreviousStock = 1500,
                NewStock = 1240,
                Timestamp = DateTime.Now.AddMinutes(-2),
                PerformedBy = "Jagadeesh (Platform Administrator)",
                ReferenceNumber = "DISP-2026-981",
                Reason = "Daily Ward Distribution - ICUs & Operating Theatres"
            },
            new StockMovement
            {
                Id = "MOV-102",
                ItemId = "ITM-001",
                ItemName = "Surgical Nitrile Gloves (Latex-Free, M)",
                MovementType = "Received",
                Quantity = 1000,
                PreviousStock = 500,
                NewStock = 1500,
                Timestamp = DateTime.Now.AddDays(-2),
                PerformedBy = "Logistics Team",
                ReferenceNumber = "PO-2026-8840",
                Reason = "Delivery Receipt from HealthSupply India"
            },
            new StockMovement
            {
                Id = "MOV-103",
                ItemId = "ITM-001",
                ItemName = "Surgical Nitrile Gloves (Latex-Free, M)",
                MovementType = "Adjusted",
                Quantity = -20,
                PreviousStock = 520,
                NewStock = 500,
                Timestamp = DateTime.Now.AddDays(-5),
                PerformedBy = "Quality Auditor",
                ReferenceNumber = "AUD-2026-04",
                Reason = "Damaged outer packaging disposal"
            },
            new StockMovement
            {
                Id = "MOV-104",
                ItemId = "ITM-003",
                ItemName = "Titanium Bone Screws 3.5mm x 24mm",
                MovementType = "Consumed",
                Quantity = -6,
                PreviousStock = 54,
                NewStock = 48,
                Timestamp = DateTime.Now.AddHours(-1),
                PerformedBy = "OT Nurse Head",
                ReferenceNumber = "SURG-CASE-882",
                Reason = "Emergency Trauma Surgery Unit 4"
            }
        });
    }

    public List<InventoryItem> GetItems() => _items.ToList();

    public InventoryItem? GetItemById(string id) => _items.FirstOrDefault(i => i.Id == id);

    public List<StockMovement> GetMovementsForItem(string itemId) =>
        _movements.Where(m => m.ItemId == itemId).OrderByDescending(m => m.Timestamp).ToList();

    public void AddItem(InventoryItem item)
    {
        item.Id = $"ITM-{_items.Count + 1:D3}";
        item.LastUpdated = DateTime.Now;
        if (item.CurrentStock <= item.ReorderLevel * 0.5)
            item.Status = StockStatus.Critical;
        else if (item.CurrentStock <= item.ReorderLevel)
            item.Status = StockStatus.LowStock;
        else if (item.CurrentStock >= item.OptimalStock * 1.3)
            item.Status = StockStatus.Overstock;
        else
            item.Status = StockStatus.Healthy;

        _items.Insert(0, item);

        _movements.Add(new StockMovement
        {
            Id = $"MOV-{_movements.Count + 1:D3}",
            ItemId = item.Id,
            ItemName = item.Name,
            MovementType = "Received",
            Quantity = item.CurrentStock,
            PreviousStock = 0,
            NewStock = item.CurrentStock,
            Timestamp = DateTime.Now,
            PerformedBy = "Jagadeesh (Administrator)",
            ReferenceNumber = "INIT-STOCK",
            Reason = "Initial Catalog Registration"
        });

        _toastService.Success("Item Added", $"{item.Name} has been cataloged under {item.Category}.");
    }

    public void AdjustStock(string itemId, int quantityChange, string reason)
    {
        var item = _items.FirstOrDefault(i => i.Id == itemId);
        if (item == null) return;

        var prev = item.CurrentStock;
        item.CurrentStock += quantityChange;
        if (item.CurrentStock < 0) item.CurrentStock = 0;
        item.LastUpdated = DateTime.Now;

        if (item.CurrentStock <= item.ReorderLevel * 0.5)
            item.Status = StockStatus.Critical;
        else if (item.CurrentStock <= item.ReorderLevel)
            item.Status = StockStatus.LowStock;
        else if (item.CurrentStock >= item.OptimalStock * 1.3)
            item.Status = StockStatus.Overstock;
        else
            item.Status = StockStatus.Healthy;

        _movements.Insert(0, new StockMovement
        {
            Id = $"MOV-{_movements.Count + 1:D3}",
            ItemId = item.Id,
            ItemName = item.Name,
            MovementType = quantityChange > 0 ? "Received" : "Consumed",
            Quantity = quantityChange,
            PreviousStock = prev,
            NewStock = item.CurrentStock,
            Timestamp = DateTime.Now,
            PerformedBy = "Jagadeesh (Platform Administrator)",
            ReferenceNumber = $"ADJ-{Random.Shared.Next(1000, 9999)}",
            Reason = reason
        });

        _toastService.Info("Stock Adjusted", $"{item.Name} stock updated from {prev} to {item.CurrentStock}.");
    }

    public (int TotalItems, int LowStock, int CriticalStock, int Overstock, int HealthyStock) GetStockMetrics()
    {
        var total = _items.Count;
        var low = _items.Count(i => i.Status == StockStatus.LowStock);
        var critical = _items.Count(i => i.Status == StockStatus.Critical);
        var overstock = _items.Count(i => i.Status == StockStatus.Overstock);
        var healthy = _items.Count(i => i.Status == StockStatus.Healthy);

        return (total, low, critical, overstock, healthy);
    }

    public string GenerateCsvExport()
    {
        var sb = new StringBuilder();
        sb.AppendLine("SKU,Product Name,Category,Location,Current Stock,Reorder Level,Unit Cost,Status,Last Updated");
        foreach (var item in _items)
        {
            sb.AppendLine($"\"{item.Sku}\",\"{item.Name}\",\"{item.Category}\",\"{item.StorageLocation}\",{item.CurrentStock},{item.ReorderLevel},{item.UnitCost},\"{item.Status}\",\"{item.LastUpdated:yyyy-MM-dd HH:mm}\"");
        }
        return sb.ToString();
    }
}

#endregion

#region Supplier Mock Service

public class SupplierService
{
    private readonly List<Supplier> _suppliers = new();
    private readonly ToastService _toastService;

    public SupplierService(ToastService toastService)
    {
        _toastService = toastService;
        SeedSuppliers();
    }

    private void SeedSuppliers()
    {
        _suppliers.Clear();
        _suppliers.AddRange(new List<Supplier>
        {
            new Supplier
            {
                Id = "SUP-01",
                Name = "HealthSupply India Pvt Ltd",
                Category = "PPE & Consumables",
                ContactPerson = "Rajesh Sharma",
                Email = "orders@healthsupply.in",
                Phone = "+91 44 2839 9012",
                Location = "Ambattur Industrial Estate, Chennai",
                ActiveOrders = 48,
                DeliveryScore = 96.4,
                AvgLeadTimeDays = 3.2,
                Status = "Excellent",
                TotalSpendYtd = 14250000m,
                Rating = 4.9,
                TotalProductsSupplied = 184
            },
            new Supplier
            {
                Id = "SUP-02",
                Name = "Apex Pharma Logistics",
                Category = "Pharmaceuticals & Cold Chain",
                ContactPerson = "Meera Nambiar",
                Email = "dispatch@apexpharma.com",
                Phone = "+91 80 4120 7761",
                Location = "Electronic City Phase 1, Bangalore",
                ActiveOrders = 34,
                DeliveryScore = 94.8,
                AvgLeadTimeDays = 2.1,
                Status = "Excellent",
                TotalSpendYtd = 22400000m,
                Rating = 4.8,
                TotalProductsSupplied = 310
            },
            new Supplier
            {
                Id = "SUP-03",
                Name = "MedTech Precision Devices",
                Category = "Surgical Implants & Hardware",
                ContactPerson = "Arun Venkatesh",
                Email = "sales@medtechprecision.com",
                Phone = "+91 40 6672 8840",
                Location = "HITEC City, Hyderabad",
                ActiveOrders = 19,
                DeliveryScore = 91.2,
                AvgLeadTimeDays = 4.5,
                Status = "Good",
                TotalSpendYtd = 8900000m,
                Rating = 4.6,
                TotalProductsSupplied = 95
            },
            new Supplier
            {
                Id = "SUP-04",
                Name = "National Parenteral Corp",
                Category = "IV Fluids & Critical Infusions",
                ContactPerson = "Sunita Rao",
                Email = "procurement@natparenteral.in",
                Phone = "+91 22 2491 3302",
                Location = "Andheri East, Mumbai",
                ActiveOrders = 26,
                DeliveryScore = 88.5,
                AvgLeadTimeDays = 5.0,
                Status = "Good",
                TotalSpendYtd = 6800000m,
                Rating = 4.4,
                TotalProductsSupplied = 42
            },
            new Supplier
            {
                Id = "SUP-05",
                Name = "Bioscan Diagnostic Systems",
                Category = "Laboratory & Diagnostics",
                ContactPerson = "Dr. Farooq Ahmed",
                Email = "support@bioscandiagnostics.com",
                Phone = "+91 44 4920 1180",
                Location = "Guindy Tech Park, Chennai",
                ActiveOrders = 12,
                DeliveryScore = 78.0,
                AvgLeadTimeDays = 7.2,
                Status = "AtRisk",
                TotalSpendYtd = 4150000m,
                Rating = 3.9,
                TotalProductsSupplied = 68
            }
        });
    }

    public List<Supplier> GetSuppliers() => _suppliers.ToList();

    public Supplier? GetSupplierById(string id) => _suppliers.FirstOrDefault(s => s.Id == id);

    public void AddSupplier(Supplier s)
    {
        s.Id = $"SUP-{_suppliers.Count + 1:D2}";
        _suppliers.Add(s);
        _toastService.Success("Supplier Added", $"{s.Name} has been enrolled in the verified vendor directory.");
    }
}

#endregion

#region Purchase Order Mock Service

public class PurchaseOrderService
{
    private readonly List<PurchaseOrder> _orders = new();
    private readonly ToastService _toastService;

    public PurchaseOrderService(ToastService toastService)
    {
        _toastService = toastService;
        SeedOrders();
    }

    private void SeedOrders()
    {
        _orders.Clear();
        _orders.AddRange(new List<PurchaseOrder>
        {
            new PurchaseOrder
            {
                Id = "PO-001",
                PoNumber = "PO-2026-8941",
                SupplierId = "SUP-01",
                SupplierName = "HealthSupply India Pvt Ltd",
                BranchId = "BR-01",
                BranchName = "Chennai • Central Hospital",
                CreatedDate = DateTime.Now.AddDays(-1),
                ExpectedDelivery = DateTime.Now.AddDays(2),
                TotalAmount = 225000m,
                Status = OrderStatus.Ordered,
                Priority = "Urgent",
                CreatedBy = "Jagadeesh (Platform Administrator)",
                ApprovedBy = "Dr. S. Ramanathan",
                Notes = "Emergency reorder for Surgical Gloves & N95 masks for ICU expansion.",
                Items = new List<PurchaseOrderItem>
                {
                    new PurchaseOrderItem { ItemId = "ITM-001", ItemName = "Surgical Nitrile Gloves (Latex-Free, M)", Sku = "MED-2048", Quantity = 500, UnitCost = 450m }
                }
            },
            new PurchaseOrder
            {
                Id = "PO-002",
                PoNumber = "PO-2026-8940",
                SupplierId = "SUP-03",
                SupplierName = "MedTech Precision Devices",
                BranchId = "BR-01",
                BranchName = "Chennai • Central Hospital",
                CreatedDate = DateTime.Now.AddHours(-4),
                ExpectedDelivery = DateTime.Now.AddDays(4),
                TotalAmount = 320000m,
                Status = OrderStatus.PendingApproval,
                Priority = "Critical",
                CreatedBy = "Procurement Officer (P. Priya)",
                Notes = "Orthopedic titanium implants for scheduled surgical procedures.",
                Items = new List<PurchaseOrderItem>
                {
                    new PurchaseOrderItem { ItemId = "ITM-003", ItemName = "Titanium Bone Screws 3.5mm x 24mm", Sku = "SUR-1044", Quantity = 100, UnitCost = 3200m }
                }
            },
            new PurchaseOrder
            {
                Id = "PO-003",
                PoNumber = "PO-2026-8939",
                SupplierId = "SUP-02",
                SupplierName = "Apex Pharma Logistics",
                BranchId = "BR-01",
                BranchName = "Chennai • Central Hospital",
                CreatedDate = DateTime.Now.AddDays(-3),
                ExpectedDelivery = DateTime.Now.AddDays(-1),
                TotalAmount = 410000m,
                Status = OrderStatus.Completed,
                Priority = "Normal",
                CreatedBy = "Jagadeesh (Platform Administrator)",
                ApprovedBy = "Dr. S. Ramanathan",
                Notes = "Monthly replenishment of anesthesia and cold-chain biologics.",
                Items = new List<PurchaseOrderItem>
                {
                    new PurchaseOrderItem { ItemId = "ITM-002", ItemName = "Propofol Injectable Emulsion 10mg/mL", Sku = "MED-8810", Quantity = 500, UnitCost = 820m }
                }
            },
            new PurchaseOrder
            {
                Id = "PO-004",
                PoNumber = "PO-2026-8938",
                SupplierId = "SUP-04",
                SupplierName = "National Parenteral Corp",
                BranchId = "BR-01",
                BranchName = "Chennai • Central Hospital",
                CreatedDate = DateTime.Now.AddDays(-4),
                ExpectedDelivery = DateTime.Now.AddDays(1),
                TotalAmount = 190000m,
                Status = OrderStatus.PartiallyReceived,
                Priority = "Normal",
                CreatedBy = "Logistics Team",
                ApprovedBy = "K. Ananya",
                Notes = "Standard IV fluid delivery. Batch 1 of 2 delivered yesterday.",
                Items = new List<PurchaseOrderItem>
                {
                    new PurchaseOrderItem { ItemId = "ITM-005", ItemName = "Normal Saline 0.9% Sodium Chloride 500mL", Sku = "IV-5510", Quantity = 500, UnitCost = 380m }
                }
            },
            new PurchaseOrder
            {
                Id = "PO-005",
                PoNumber = "PO-2026-8937",
                SupplierId = "SUP-05",
                SupplierName = "Bioscan Diagnostic Systems",
                BranchId = "BR-01",
                BranchName = "Chennai • Central Hospital",
                CreatedDate = DateTime.Now.AddDays(-6),
                ExpectedDelivery = DateTime.Now.AddDays(3),
                TotalAmount = 225000m,
                Status = OrderStatus.Approved,
                Priority = "Normal",
                CreatedBy = "Lab Director",
                ApprovedBy = "Dr. S. Ramanathan",
                Notes = "Diagnostic cartridges for emergency pathology.",
                Items = new List<PurchaseOrderItem>
                {
                    new PurchaseOrderItem { ItemId = "ITM-006", ItemName = "Troponin-I Rapid Diagnostic Test Cartridges", Sku = "LAB-3011", Quantity = 50, UnitCost = 4500m }
                }
            }
        });
    }

    public List<PurchaseOrder> GetOrders() => _orders.OrderByDescending(o => o.CreatedDate).ToList();

    public PurchaseOrder? GetOrderById(string id) => _orders.FirstOrDefault(o => o.Id == id);

    public void ApproveOrder(string orderId)
    {
        var po = _orders.FirstOrDefault(o => o.Id == orderId);
        if (po != null)
        {
            po.Status = OrderStatus.Approved;
            po.ApprovedBy = "Jagadeesh (Platform Administrator)";
            _toastService.Success("Order Approved", $"Purchase Order {po.PoNumber} approved for ₹{po.TotalAmount:N0}.");
        }
    }

    public void ReceiveOrder(string orderId)
    {
        var po = _orders.FirstOrDefault(o => o.Id == orderId);
        if (po != null)
        {
            po.Status = OrderStatus.Completed;
            _toastService.Success("Stock Received", $"All items for {po.PoNumber} checked in and inventory reconciled.");
        }
    }

    public void CreateOrder(PurchaseOrder order)
    {
        order.Id = $"PO-{_orders.Count + 1:D3}";
        order.PoNumber = $"PO-2026-{Random.Shared.Next(8950, 9999)}";
        order.CreatedDate = DateTime.Now;
        order.CreatedBy = "Jagadeesh (Platform Administrator)";
        order.Status = OrderStatus.PendingApproval;
        order.TotalAmount = order.Items.Sum(i => i.TotalCost);

        _orders.Insert(0, order);
        _toastService.Success("Purchase Order Created", $"{order.PoNumber} generated for {order.SupplierName}.");
    }
}

#endregion

#region Document Vault Mock Service

public class DocumentVaultService
{
    private readonly List<DocumentRecord> _documents = new();
    private readonly ToastService _toastService;

    public DocumentVaultService(ToastService toastService)
    {
        _toastService = toastService;
        SeedDocuments();
    }

    private void SeedDocuments()
    {
        _documents.Clear();
        _documents.AddRange(new List<DocumentRecord>
        {
            new DocumentRecord
            {
                Id = "DOC-01",
                Name = "HealthSupply_Invoice_INV-88219.pdf",
                Category = "Invoices",
                FileType = "PDF",
                FileSize = "1.8 MB",
                Owner = "Jagadeesh (Administrator)",
                UploadedAt = DateTime.Now.AddMinutes(-35),
                SecurityBadge = "Tenant Protected",
                S3StoragePath = "s3://orgshield-ten001-vault/invoices/2026/09/inv-88219.enc",
                Sha256Hash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"
            },
            new DocumentRecord
            {
                Id = "DOC-02",
                Name = "FDA_Compliance_Audit_Report_Q3_2026.pdf",
                Category = "Compliance",
                FileType = "PDF",
                FileSize = "4.2 MB",
                Owner = "Dr. S. Ramanathan",
                UploadedAt = DateTime.Now.AddHours(-4),
                SecurityBadge = "Tenant Protected",
                S3StoragePath = "s3://orgshield-ten001-vault/compliance/fda-q3-2026.enc",
                Sha256Hash = "8f434346648f6b96df89dda901c5176b10a6d83961dd3c1ac88b59b2dc327aa4"
            },
            new DocumentRecord
            {
                Id = "DOC-03",
                Name = "PurchaseOrder_PO-2026-8941_Signed.pdf",
                Category = "Purchase Orders",
                FileType = "PDF",
                FileSize = "980 KB",
                Owner = "Procurement Desk",
                UploadedAt = DateTime.Now.AddHours(-18),
                SecurityBadge = "Tenant Protected",
                S3StoragePath = "s3://orgshield-ten001-vault/orders/po-8941-signed.enc",
                Sha256Hash = "ca978112ca1bbdcafac231b39a23dc4da786eff8147c4e72b9807785afee48bb"
            },
            new DocumentRecord
            {
                Id = "DOC-04",
                Name = "Propofol_ColdChain_Log_Certificate.pdf",
                Category = "Product Documents",
                FileType = "PDF",
                FileSize = "2.1 MB",
                Owner = "Apex Pharma QA",
                UploadedAt = DateTime.Now.AddDays(-1),
                SecurityBadge = "Tenant Protected",
                S3StoragePath = "s3://orgshield-ten001-vault/specs/propofol-cc-cert.enc",
                Sha256Hash = "fb8e20fc2e4c3f248c60c39bd652f3c1347298ab9f5a7a8d8c973a0e6988849b"
            },
            new DocumentRecord
            {
                Id = "DOC-05",
                Name = "NABH_Hospital_Accreditation_Certificate_2026.pdf",
                Category = "Compliance",
                FileType = "PDF",
                FileSize = "5.6 MB",
                Owner = "Chief Operations",
                UploadedAt = DateTime.Now.AddDays(-2),
                SecurityBadge = "Tenant Protected",
                S3StoragePath = "s3://orgshield-ten001-vault/compliance/nabh-2026.enc",
                Sha256Hash = "1a8565a9d214a3850065b3a46c7e2302901142f3560232f4599be69b60472940"
            },
            new DocumentRecord
            {
                Id = "DOC-06",
                Name = "Monthly_Inventory_Valuation_Report_Aug.pdf",
                Category = "Reports",
                FileType = "PDF",
                FileSize = "3.4 MB",
                Owner = "Financial Controller",
                UploadedAt = DateTime.Now.AddDays(-4),
                SecurityBadge = "Tenant Protected",
                S3StoragePath = "s3://orgshield-ten001-vault/reports/val-aug-2026.enc",
                Sha256Hash = "6b86b273ff34fce19d6b804eff5a3f5747ada4eaa22f1d49c01e52ddb7875b4b"
            }
        });
    }

    public List<DocumentRecord> GetDocuments() => _documents.OrderByDescending(d => d.UploadedAt).ToList();

    public void UploadDocument(string name, string category, string size)
    {
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(name + DateTime.Now.Ticks)));
        var doc = new DocumentRecord
        {
            Id = $"DOC-{_documents.Count + 1:D2}",
            Name = name,
            Category = category,
            FileType = name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ? "PDF" : "DOCX",
            FileSize = size,
            Owner = "Jagadeesh (Platform Administrator)",
            UploadedAt = DateTime.Now,
            SecurityBadge = "Tenant Protected",
            S3StoragePath = $"s3://orgshield-ten001-vault/{category.ToLowerInvariant().Replace(" ", "-")}/{name}.enc",
            Sha256Hash = hash.ToLowerInvariant()
        };

        _documents.Insert(0, doc);
        _toastService.Success("Encrypted Document Stored", $"{doc.Name} uploaded and encrypted with AES-256 GCM in tenant storage.");
    }
}

#endregion

#region Audit Log Mock Service

public class AuditLogService
{
    private readonly List<AuditLog> _logs = new();
    private readonly ToastService _toastService;

    public event Action? OnLogAdded;

    public AuditLogService(ToastService toastService)
    {
        _toastService = toastService;
        SeedLogs();
    }

    private void SeedLogs()
    {
        _logs.Clear();
        _logs.AddRange(new List<AuditLog>
        {
            new AuditLog
            {
                Id = "AUD-9912",
                Timestamp = DateTime.Now.AddMinutes(-2),
                User = "Jagadeesh",
                UserRole = "Platform Administrator",
                Action = "Updated Inventory Stock",
                Resource = "Inventory",
                ResourceId = "ITM-001 (Surgical Nitrile Gloves)",
                IpAddress = "192.168.1.104",
                Severity = "Info",
                Details = "Decreased stock by 260 units for Daily Ward Distribution.",
                PreviousState = "Stock: 1,500 Units",
                NewState = "Stock: 1,240 Units"
            },
            new AuditLog
            {
                Id = "AUD-9911",
                Timestamp = DateTime.Now.AddMinutes(-18),
                User = "Admin (Jagadeesh)",
                UserRole = "Platform Administrator",
                Action = "Uploaded Encrypted Document",
                Resource = "Document Vault",
                ResourceId = "DOC-01 (HealthSupply_Invoice_INV-88219.pdf)",
                IpAddress = "192.168.1.104",
                Severity = "Info",
                Details = "Uploaded invoice with KMS AES-256 envelope encryption.",
                PreviousState = "null",
                NewState = "SHA256: e3b0c442...991b7852b855"
            },
            new AuditLog
            {
                Id = "AUD-9910",
                Timestamp = DateTime.Now.AddMinutes(-32),
                User = "Security Engine (WAF Guard)",
                UserRole = "System Daemon",
                Action = "Blocked Cross-Tenant Access Attempt",
                Resource = "Tenant Isolation Barrier",
                ResourceId = "TEN-002 -> TEN-001 Bridge Target",
                IpAddress = "203.0.113.88",
                Severity = "Critical",
                Details = "Foreign tenant ID token attempted to query tenant TEN-001 patient supply registry. Request dropped with HTTP 403 Forbidden.",
                PreviousState = "Tenant Scope: TEN-002",
                NewState = "Action: Dropped & Blacklisted IP"
            },
            new AuditLog
            {
                Id = "AUD-9909",
                Timestamp = DateTime.Now.AddHours(-2),
                User = "Dr. S. Ramanathan",
                UserRole = "Chief Operations Officer",
                Action = "Approved Purchase Order",
                Resource = "Purchase Orders",
                ResourceId = "PO-2026-8941",
                IpAddress = "192.168.1.42",
                Severity = "Info",
                Details = "Digitally signed PO-2026-8941 for ₹225,000 to HealthSupply India.",
                PreviousState = "Status: PendingApproval",
                NewState = "Status: Approved"
            },
            new AuditLog
            {
                Id = "AUD-9908",
                Timestamp = DateTime.Now.AddHours(-5),
                User = "Procurement Officer (P. Priya)",
                UserRole = "Procurement Manager",
                Action = "Created Purchase Order",
                Resource = "Purchase Orders",
                ResourceId = "PO-2026-8940",
                IpAddress = "192.168.1.55",
                Severity = "Info",
                Details = "Drafted critical PO for Titanium Bone Screws.",
                PreviousState = "null",
                NewState = "Status: PendingApproval (₹320,000)"
            },
            new AuditLog
            {
                Id = "AUD-9907",
                Timestamp = DateTime.Now.AddHours(-9),
                User = "Automated Health Probe",
                UserRole = "System Health Watcher",
                Action = "Cold Chain Temperature Verification",
                Resource = "Storage Facility",
                ResourceId = "Cold Vault 02 (Propofol Storage)",
                IpAddress = "10.0.4.12",
                Severity = "Info",
                Details = "Temperature stable at 4.2°C (Optimal threshold: 2°C - 8°C).",
                PreviousState = "4.1°C",
                NewState = "4.2°C"
            }
        });
    }

    public List<AuditLog> GetLogs() => _logs.OrderByDescending(l => l.Timestamp).ToList();

    public void AddLog(AuditLog log)
    {
        log.Id = $"AUD-{_logs.Count + 9900}";
        log.Timestamp = DateTime.Now;
        _logs.Insert(0, log);
        OnLogAdded?.Invoke();
    }
}

#endregion

#region Security Center & Tenant Isolation Test Service

public class SecurityCenterService
{
    private readonly ToastService _toastService;
    private readonly AuditLogService _auditLogService;

    public bool IsTestRunning { get; private set; }
    public List<SecurityTestStep> TestSteps { get; private set; } = new();
    public int OverallSecurityScore { get; private set; } = 96;

    public event Action? OnTestStateChanged;

    public SecurityCenterService(ToastService toastService, AuditLogService auditLogService)
    {
        _toastService = toastService;
        _auditLogService = auditLogService;
        ResetTestSteps();
    }

    public void ResetTestSteps()
    {
        TestSteps = new List<SecurityTestStep>
        {
            new SecurityTestStep { StepNumber = "01", TestName = "Tenant A → Tenant A Internal Query", SourceContext = "Tenant TEN-001 (Apollo)", TargetContext = "Tenant TEN-001 (Apollo)", ExpectedOutcome = "PASS", Status = "Pass", DurationMs = 24, LogDetail = "Database row-level security policy verified. 12,840 records in scope." },
            new SecurityTestStep { StepNumber = "02", TestName = "Tenant B → Tenant B Internal Query", SourceContext = "Tenant TEN-002 (MedCare)", TargetContext = "Tenant TEN-002 (MedCare)", ExpectedOutcome = "PASS", Status = "Pass", DurationMs = 18, LogDetail = "Isolated partition query resolved cleanly. Zero cross-boundary leakage." },
            new SecurityTestStep { StepNumber = "03", TestName = "Tenant A → Tenant B Cross-Boundary Infiltration", SourceContext = "Tenant TEN-001 (Apollo)", TargetContext = "Tenant TEN-002 (MedCare)", ExpectedOutcome = "BLOCKED", Status = "Blocked", DurationMs = 12, LogDetail = "Security barrier intercepted invalid tenant token. HTTP 403 Forbidden emitted." },
            new SecurityTestStep { StepNumber = "04", TestName = "Tenant B → Tenant A Cross-Boundary Infiltration", SourceContext = "Tenant TEN-002 (MedCare)", TargetContext = "Tenant TEN-001 (Apollo)", ExpectedOutcome = "BLOCKED", Status = "Blocked", DurationMs = 15, LogDetail = "Foreign KMS decryption token rejected. Zero payload leaked." },
            new SecurityTestStep { StepNumber = "05", TestName = "Encrypted S3 Storage Path Isolation", SourceContext = "IAM Role: ApolloVaultService", TargetContext = "s3://orgshield-ten002-vault/*", ExpectedOutcome = "PASS", Status = "Pass", DurationMs = 31, LogDetail = "Bucket policy denied cross-tenant S3 GetObject. Envelope encryption intact." },
            new SecurityTestStep { StepNumber = "06", TestName = "Role Boundary & Multi-Tenant RBAC Validation", SourceContext = "User: InventoryManager (TEN-001)", TargetContext = "Admin Config (TEN-001)", ExpectedOutcome = "PASS", Status = "Pass", DurationMs = 14, LogDetail = "Granular privilege verified. Elevation without MFA blocked." }
        };
    }

    public async Task RunSimulatedSecurityTestAsync()
    {
        if (IsTestRunning) return;

        IsTestRunning = true;
        foreach (var step in TestSteps)
        {
            step.Status = "Pending";
        }
        OnTestStateChanged?.Invoke();

        _toastService.Info("Security Suite Initiated", "Running 6/6 Multi-Tenant Isolation & Zero-Trust Verification Tests...");

        for (int i = 0; i < TestSteps.Count; i++)
        {
            TestSteps[i].Status = "Running";
            OnTestStateChanged?.Invoke();
            await Task.Delay(600); // realistic smooth progression

            if (i == 2 || i == 3)
            {
                TestSteps[i].Status = "Blocked";
            }
            else
            {
                TestSteps[i].Status = "Pass";
            }
            OnTestStateChanged?.Invoke();
        }

        IsTestRunning = false;
        OverallSecurityScore = 98;
        OnTestStateChanged?.Invoke();

        _auditLogService.AddLog(new AuditLog
        {
            User = "Jagadeesh (Administrator)",
            UserRole = "Platform Administrator",
            Action = "Executed Tenant Isolation Security Test Suite",
            Resource = "Zero-Trust Security Engine",
            ResourceId = "TEST-RUN-6X",
            IpAddress = "192.168.1.104",
            Severity = "Info",
            Details = "6/6 automated multi-tenant isolation and cryptographic containment tests passed with zero leakage."
        });

        _toastService.Success("Security Test Complete", "All 6 multi-tenant isolation controls passed with zero boundary leakage!");
    }
}

#endregion

#region Alert Mock Service

public class AlertService
{
    private readonly List<AlertItem> _alerts = new();
    public event Action? OnAlertsChanged;

    public AlertService()
    {
        SeedAlerts();
    }

    private void SeedAlerts()
    {
        _alerts.Clear();
        _alerts.AddRange(new List<AlertItem>
        {
            new AlertItem
            {
                Id = "ALT-01",
                Title = "Unauthorized Cross-Tenant Access Blocked",
                Description = "Foreign tenant ID token attempted to query tenant TEN-001 patient supply registry from IP 203.0.113.88. Attack blocked.",
                Timestamp = DateTime.Now.AddMinutes(-32),
                Severity = "Critical",
                Category = "Security",
                IsRead = false,
                ActionText = "View in Security Center",
                ActionUrl = "/security"
            },
            new AlertItem
            {
                Id = "ALT-02",
                Title = "Surgical Nitrile Gloves Below Reorder Level",
                Description = "Stock remaining is 1,240 units (Reorder threshold: 2,000 units). Estimated depletion in 6 days.",
                Timestamp = DateTime.Now.AddMinutes(-2),
                Severity = "Warning",
                Category = "Inventory",
                IsRead = false,
                ActionText = "Create Purchase Order",
                ActionUrl = "/orders"
            },
            new AlertItem
            {
                Id = "ALT-03",
                Title = "Purchase Order Awaiting Approval",
                Description = "PO-2026-8940 for Titanium Bone Screws (₹320,000) requires administrative sign-off.",
                Timestamp = DateTime.Now.AddHours(-4),
                Severity = "Attention",
                Category = "Orders",
                IsRead = false,
                ActionText = "Review Order",
                ActionUrl = "/orders"
            },
            new AlertItem
            {
                Id = "ALT-04",
                Title = "Supplier Delivery Completed",
                Description = "HealthSupply India delivery of 1,000 units checked in and verified at Chennai Central Hospital.",
                Timestamp = DateTime.Now.AddDays(-2),
                Severity = "Resolved",
                Category = "Supply Chain",
                IsRead = true,
                ActionText = "View Log",
                ActionUrl = "/inventory"
            }
        });
    }

    public List<AlertItem> GetAlerts() => _alerts.OrderByDescending(a => a.Timestamp).ToList();
    public int UnreadCount => _alerts.Count(a => !a.IsRead);

    public void MarkAsRead(string id)
    {
        var alert = _alerts.FirstOrDefault(a => a.Id == id);
        if (alert != null)
        {
            alert.IsRead = true;
            OnAlertsChanged?.Invoke();
        }
    }

    public void MarkAllAsRead()
    {
        foreach (var a in _alerts) a.IsRead = true;
        OnAlertsChanged?.Invoke();
    }
}

#endregion

#region AI Insights Service

public class AIInsightService
{
    public List<AIInsight> GetInsights()
    {
        return new List<AIInsight>
        {
            new AIInsight
            {
                Id = "INS-01",
                Title = "Reorder Recommended",
                Subtitle = "Surgical Nitrile Gloves",
                Type = "Reorder",
                ImpactText = "Current stock is 1,240 boxes. Based on current 30-day burn rate across 8 ICUs, stock will reach zero in 6 days.",
                ItemName = "Surgical Nitrile Gloves",
                CurrentStock = 1240,
                EstimatedRemainingDays = 6,
                RecommendedOrderUnits = 5000,
                ActionButtonText = "Review Recommendation"
            },
            new AIInsight
            {
                Id = "INS-02",
                Title = "Optimization Opportunity",
                Subtitle = "Consolidated Delivery Schedules",
                Type = "Optimization",
                ImpactText = "3 suppliers (HealthSupply, Apex, MedTech) have overlapping Tuesday logistics corridors. Consolidating shipments reduces dock handling fees.",
                PotentialSaving = "₹42,000 / month",
                ActionButtonText = "View Analysis"
            },
            new AIInsight
            {
                Id = "INS-03",
                Title = "Cold-Chain Anomaly Prevention",
                Subtitle = "Propofol Storage Optimization",
                Type = "Security",
                ImpactText = "Thermal sensor log indicates minor ambient fluctuation during 3 PM ward restocking. Pre-cooling chamber before transfer is advised.",
                ActionButtonText = "Inspect Thermal Logs"
            }
        };
    }
}

#endregion

#region Command Palette Service

public class CommandPaletteService
{
    public bool IsOpen { get; private set; }
    public event Action? OnVisibilityChanged;

    public void Open()
    {
        IsOpen = true;
        OnVisibilityChanged?.Invoke();
    }

    public void Close()
    {
        IsOpen = false;
        OnVisibilityChanged?.Invoke();
    }

    public void Toggle()
    {
        IsOpen = !IsOpen;
        OnVisibilityChanged?.Invoke();
    }
}

#endregion
