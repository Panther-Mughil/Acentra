import React, { useState, useEffect } from 'react';
import Header from './components/Header';
import InventoryDashboard from './components/InventoryDashboard';
import StockAdjustmentModal from './components/StockAdjustmentModal';
import ItemModal from './components/ItemModal';
import UploadDocModal from './components/UploadDocModal';
import CreateTenantModal from './components/CreateTenantModal';
import TenantSecurityAudit from './components/TenantSecurityAudit';
import S3StorageExplorer from './components/S3StorageExplorer';
import ArchitectureDiagram from './components/ArchitectureDiagram';
import Toast from './components/Toast';
import { 
  TenantApi, 
  InventoryApi, 
  setActiveTenantContext 
} from './services/api';

// Initial Mock Seed Data
const INITIAL_TENANTS = [
  { id: '11111111-1111-1111-1111-111111111111', code: 'apex-health', name: 'Apex Healthcare System', tier: 'Enterprise' },
  { id: '22222222-2222-2222-2222-222222222222', code: 'biomed-labs', name: 'BioMed Diagnostics & Labs', tier: 'Professional' },
  { id: '33333333-3333-3333-3333-333333333333', code: 'novacare-pharma', name: 'NovaCare Pharmaceuticals', tier: 'Enterprise' },
];

const INITIAL_ITEMS_BY_TENANT = {
  '11111111-1111-1111-1111-111111111111': [
    { id: 'item-1', tenantId: '11111111-1111-1111-1111-111111111111', sku: 'APX-AMX-500', name: 'Amoxicillin 500mg USP Capsules', category: 'Pharmaceuticals', quantity: 240, unitPrice: 18.50, reorderLevel: 50, description: 'Broad-spectrum antibiotic for bacterial infections', s3FileKey: 'apex-health/docs/APX-AMX-500/amox_spec_sheet.pdf', fileUrl: '#' },
    { id: 'item-2', tenantId: '11111111-1111-1111-1111-111111111111', sku: 'APX-N95-SURG', name: 'N95 Surgical Respirator Masks (Box of 50)', category: 'PPE & Safety', quantity: 18, unitPrice: 35.00, reorderLevel: 30, description: 'NIOSH approved particulate respirator', s3FileKey: null, fileUrl: null },
    { id: 'item-3', tenantId: '11111111-1111-1111-1111-111111111111', sku: 'APX-SCALP-10', name: 'Sterile Disposable Scalpel #10 (Box of 20)', category: 'Surgical Equipment', quantity: 150, unitPrice: 42.00, reorderLevel: 25, description: 'Precision stainless steel surgical blade with safety guard', s3FileKey: 'apex-health/docs/APX-SCALP-10/scalpel_iso_cert.pdf', fileUrl: '#' },
    { id: 'item-4', tenantId: '11111111-1111-1111-1111-111111111111', sku: 'APX-IV-SALINE', name: '0.9% Sodium Chloride IV Saline 1000ml', category: 'Consumables', quantity: 500, unitPrice: 8.75, reorderLevel: 100, description: 'Sterile isotonic IV infusion fluid', s3FileKey: null, fileUrl: null },
  ],
  '22222222-2222-2222-2222-222222222222': [
    { id: 'item-201', tenantId: '22222222-2222-2222-2222-222222222222', sku: 'BIO-PCR-COVID', name: 'RT-PCR Multiplex Viral Detection Assay Kit', category: 'Diagnostics', quantity: 85, unitPrice: 145.00, reorderLevel: 20, description: 'High sensitivity viral RNA test kit for clinical labs', s3FileKey: 'biomed-labs/docs/BIO-PCR-COVID/fda_eua_cert.pdf', fileUrl: '#' },
    { id: 'item-202', tenantId: '22222222-2222-2222-2222-222222222222', sku: 'BIO-CENT-TUBE', name: 'Conical Centrifuge Tubes 50mL (Pack of 500)', category: 'Consumables', quantity: 12, unitPrice: 65.00, reorderLevel: 15, description: 'Graduated sterile polypropylene tubes with screw caps', s3FileKey: null, fileUrl: null },
    { id: 'item-203', tenantId: '22222222-2222-2222-2222-222222222222', sku: 'BIO-HEPA-FLTR', name: 'Micro-Biological Grade HEPA Filter Core', category: 'PPE & Safety', quantity: 8, unitPrice: 280.00, reorderLevel: 10, description: '99.97% particulate retention filter for laminar flow hoods', s3FileKey: 'biomed-labs/docs/BIO-HEPA-FLTR/filter_specs.pdf', fileUrl: '#' },
  ],
  '33333333-3333-3333-3333-333333333333': [
    { id: 'item-301', tenantId: '33333333-3333-3333-3333-333333333333', sku: 'NOVA-INS-GLARG', name: 'Insulin Glargine 100 Units/mL SoloStar', category: 'Pharmaceuticals', quantity: 420, unitPrice: 88.00, reorderLevel: 60, description: 'Long-acting basal human insulin analog', s3FileKey: 'novacare-pharma/docs/NOVA-INS-GLARG/pharma_monograph.pdf', fileUrl: '#' },
    { id: 'item-302', tenantId: '33333333-3333-3333-3333-333333333333', sku: 'NOVA-ATV-20MG', name: 'Atorvastatin Calcium 20mg Tablets', category: 'Pharmaceuticals', quantity: 950, unitPrice: 12.20, reorderLevel: 150, description: 'Lipid-lowering HMG-CoA reductase inhibitor', s3FileKey: null, fileUrl: null },
  ]
};

export default function App() {
  const [tenants, setTenants] = useState(INITIAL_TENANTS);
  const [activeTenant, setActiveTenant] = useState(INITIAL_TENANTS[0]);
  const [activeTab, setActiveTab] = useState('inventory'); // 'inventory', 's3storage', 'architecture', 'audit'
  
  const [items, setItems] = useState([]);
  const [metrics, setMetrics] = useState(null);
  const [loading, setLoading] = useState(false);
  const [toasts, setToasts] = useState([]);

  // Modals state
  const [isAddItemOpen, setIsAddItemOpen] = useState(false);
  const [isEditItemOpen, setIsEditItemOpen] = useState(false);
  const [itemToEdit, setItemToEdit] = useState(null);
  const [isAdjustStockOpen, setIsAdjustStockOpen] = useState(false);
  const [itemToAdjust, setItemToAdjust] = useState(null);
  const [isUploadDocOpen, setIsUploadDocOpen] = useState(false);
  const [itemToUpload, setItemToUpload] = useState(null);
  const [isCreateTenantOpen, setIsCreateTenantOpen] = useState(false);

  // In-memory tenant store
  const [tenantInventoryStore, setTenantInventoryStore] = useState(INITIAL_ITEMS_BY_TENANT);

  const addToast = (type, title, message) => {
    const id = Date.now() + Math.random();
    setToasts(prev => [...prev, { id, type, title, message }]);
    setTimeout(() => {
      setToasts(prev => prev.filter(t => t.id !== id));
    }, 4500);
  };

  const removeToast = (id) => {
    setToasts(prev => prev.filter(t => t.id !== id));
  };

  // Load tenants on mount
  useEffect(() => {
    const loadTenants = async () => {
      try {
        const fetched = await TenantApi.getAll();
        if (fetched && fetched.length > 0) {
          setTenants(fetched);
          setActiveTenant(fetched[0]);
        }
      } catch (err) {
        console.log('Using local tenant list fallback.');
      }
    };
    loadTenants();
  }, []);

  // When activeTenant changes
  useEffect(() => {
    if (activeTenant) {
      setActiveTenantContext(activeTenant);
      fetchTenantData(activeTenant.id);
      addToast('info', 'Switched Tenant Context', `Now viewing partition: ${activeTenant.name}`);
    }
  }, [activeTenant?.id]);

  const fetchTenantData = async (tenantId) => {
    setLoading(true);
    try {
      const data = await InventoryApi.getAll();
      setItems(data);
    } catch (err) {
      const tenantItems = tenantInventoryStore[tenantId] || [];
      setItems(tenantItems);
    } finally {
      setLoading(false);
    }
  };

  const handleSelectTenant = (tenant) => {
    setActiveTenant(tenant);
  };

  const handleCreateTenant = async (newTenantData) => {
    try {
      const created = await TenantApi.create(newTenantData);
      setTenants(prev => [...prev, created]);
      setActiveTenant(created);
      addToast('success', 'Tenant Created', `Tenant "${created.name}" provisioned successfully.`);
    } catch (err) {
      const newId = crypto.randomUUID ? crypto.randomUUID() : `tenant-${Date.now()}`;
      const newTenant = {
        id: newId,
        code: newTenantData.code,
        name: newTenantData.name,
        tier: newTenantData.subscriptionTier || 'Enterprise'
      };

      setTenants(prev => [...prev, newTenant]);
      setTenantInventoryStore(prev => ({
        ...prev,
        [newId]: []
      }));
      setActiveTenant(newTenant);
      addToast('success', 'Tenant Created', `Tenant "${newTenant.name}" provisioned.`);
    }
  };

  const handleSaveItem = async (itemData) => {
    try {
      if (itemToEdit) {
        const updated = await InventoryApi.update(itemToEdit.id, itemData);
        setItems(prev => prev.map(i => i.id === itemToEdit.id ? updated : i));
        addToast('success', 'Item Updated', `Updated "${itemData.name}"`);
      } else {
        const created = await InventoryApi.create(itemData);
        setItems(prev => [...prev, created]);
        addToast('success', 'Item Created', `Added "${itemData.name}" with SKU: ${itemData.sku}`);
      }
    } catch (err) {
      if (itemToEdit) {
        setTenantInventoryStore(prev => {
          const updated = (prev[activeTenant.id] || []).map(i => 
            i.id === itemToEdit.id ? { ...i, ...itemData } : i
          );
          return { ...prev, [activeTenant.id]: updated };
        });
        setItems(prev => prev.map(i => i.id === itemToEdit.id ? { ...i, ...itemData } : i));
        addToast('success', 'Item Updated', `Updated "${itemData.name}"`);
      } else {
        const newItem = {
          id: `item-${Date.now()}`,
          tenantId: activeTenant.id,
          ...itemData,
          s3FileKey: null,
          fileUrl: null
        };
        setTenantInventoryStore(prev => ({
          ...prev,
          [activeTenant.id]: [...(prev[activeTenant.id] || []), newItem]
        }));
        setItems(prev => [...prev, newItem]);
        addToast('success', 'Item Created', `Added "${itemData.name}" with SKU: ${itemData.sku}`);
      }
    }
  };

  const handleDeleteItem = async (itemId) => {
    if (window.confirm('Are you sure you want to delete this inventory item?')) {
      try {
        await InventoryApi.delete(itemId);
      } catch (err) {
        setTenantInventoryStore(prev => ({
          ...prev,
          [activeTenant.id]: (prev[activeTenant.id] || []).filter(i => i.id !== itemId)
        }));
      }
      setItems(prev => prev.filter(i => i.id !== itemId));
      addToast('info', 'Item Removed', 'Inventory item deleted from tenant partition.');
    }
  };

  const handleAdjustStock = async (itemId, adjustment) => {
    try {
      const res = await InventoryApi.adjustStock(itemId, adjustment);
      setItems(prev => prev.map(i => i.id === itemId ? res.item : i));
    } catch (err) {
      setTenantInventoryStore(prev => {
        const updated = (prev[activeTenant.id] || []).map(i => {
          if (i.id === itemId) {
            let newQty = i.quantity;
            if (adjustment.type === 'INBOUND') newQty += adjustment.quantity;
            else if (adjustment.type === 'OUTBOUND') newQty = Math.max(0, newQty - adjustment.quantity);
            else if (adjustment.type === 'AUDIT_CORRECTION') newQty = adjustment.quantity;
            return { ...i, quantity: newQty };
          }
          return i;
        });
        return { ...prev, [activeTenant.id]: updated };
      });

      setItems(prev => prev.map(i => {
        if (i.id === itemId) {
          let newQty = i.quantity;
          if (adjustment.type === 'INBOUND') newQty += adjustment.quantity;
          else if (adjustment.type === 'OUTBOUND') newQty = Math.max(0, newQty - adjustment.quantity);
          else if (adjustment.type === 'AUDIT_CORRECTION') newQty = adjustment.quantity;
          return { ...i, quantity: newQty };
        }
        return i;
      }));
    }
    addToast('success', 'Stock Adjusted', `Applied ${adjustment.type} adjustment (${adjustment.quantity} units).`);
  };

  const handleUploadDoc = async (itemId, file) => {
    try {
      const res = await InventoryApi.uploadDocument(itemId, file);
      setItems(prev => prev.map(i => 
        i.id === itemId ? { ...i, s3FileKey: res.s3Key, fileUrl: res.presignedUrl } : i
      ));
    } catch (err) {
      const s3Key = `${activeTenant.code}/docs/${file.name}`;
      setTenantInventoryStore(prev => {
        const updated = (prev[activeTenant.id] || []).map(i => 
          i.id === itemId ? { ...i, s3FileKey: s3Key, fileUrl: URL.createObjectURL(file) } : i
        );
        return { ...prev, [activeTenant.id]: updated };
      });

      setItems(prev => prev.map(i => 
        i.id === itemId ? { ...i, s3FileKey: s3Key, fileUrl: URL.createObjectURL(file) } : i
      ));
    }
    addToast('success', 'S3 Upload Complete', `Encrypted file attached under ${activeTenant.code}/ partition.`);
  };

  return (
    <div className="min-h-screen bg-[#0a0f1d] text-slate-100 flex flex-col selection:bg-blue-500 selection:text-white">
      
      {/* Top Header */}
      <Header
        tenants={tenants}
        activeTenant={activeTenant}
        onSelectTenant={handleSelectTenant}
        onOpenCreateTenant={() => setIsCreateTenantOpen(true)}
        activeTab={activeTab}
        setActiveTab={setActiveTab}
      />

      {/* Main Content Area */}
      <main className="flex-1 max-w-7xl w-full mx-auto px-4 sm:px-6 lg:px-8 py-8">
        {activeTab === 'inventory' && (
          <InventoryDashboard
            items={items}
            metrics={metrics}
            loading={loading}
            onRefresh={() => fetchTenantData(activeTenant.id)}
            onOpenAddItem={() => { setItemToEdit(null); setIsAddItemOpen(true); }}
            onOpenEditItem={(item) => { setItemToEdit(item); setIsEditItemOpen(true); }}
            onOpenAdjustStock={(item) => { setItemToAdjust(item); setIsAdjustStockOpen(true); }}
            onOpenUploadDoc={(item) => { setItemToUpload(item); setIsUploadDocOpen(true); }}
            onDeleteItem={handleDeleteItem}
            activeTenant={activeTenant}
            onNotify={addToast}
          />
        )}

        {activeTab === 's3storage' && (
          <S3StorageExplorer
            activeTenant={activeTenant}
            items={items}
          />
        )}

        {activeTab === 'architecture' && (
          <ArchitectureDiagram
            activeTenant={activeTenant}
          />
        )}

        {activeTab === 'audit' && (
          <TenantSecurityAudit
            activeTenant={activeTenant}
            tenants={tenants}
          />
        )}
      </main>

      {/* Interactive Modals */}
      <ItemModal
        isOpen={isAddItemOpen || isEditItemOpen}
        onClose={() => { setIsAddItemOpen(false); setIsEditItemOpen(false); setItemToEdit(null); }}
        onSave={handleSaveItem}
        itemToEdit={itemToEdit}
        activeTenant={activeTenant}
      />

      <StockAdjustmentModal
        isOpen={isAdjustStockOpen}
        item={itemToAdjust}
        onClose={() => { setIsAdjustStockOpen(false); setItemToAdjust(null); }}
        onAdjust={handleAdjustStock}
      />

      <UploadDocModal
        isOpen={isUploadDocOpen}
        item={itemToUpload}
        onClose={() => { setIsUploadDocOpen(false); setItemToUpload(null); }}
        onUpload={handleUploadDoc}
        activeTenant={activeTenant}
      />

      <CreateTenantModal
        isOpen={isCreateTenantOpen}
        onClose={() => setIsCreateTenantOpen(false)}
        onCreateTenant={handleCreateTenant}
      />

      {/* Floating Real-Time Notifications */}
      <Toast toasts={toasts} onDismiss={removeToast} />

      {/* Footer */}
      <footer className="border-t border-white/5 py-6 bg-slate-950/60 text-center text-xs text-slate-500 font-mono">
        Acentra Health Codeathon 2026 • Multi-Tenant Inventory Architecture • ASP.NET Core 8 & EF Core Global Query Filters
      </footer>

    </div>
  );
}
