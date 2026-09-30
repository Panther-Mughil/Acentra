import React, { useState } from 'react';
import { 
  Package, 
  DollarSign, 
  AlertTriangle, 
  Search, 
  Filter, 
  Plus, 
  ArrowUpDown, 
  UploadCloud, 
  Edit3, 
  Trash2, 
  FileText, 
  TrendingUp,
  Boxes,
  CheckCircle,
  ExternalLink,
  RefreshCw
} from 'lucide-react';

export default function InventoryDashboard({ 
  items, 
  metrics, 
  loading, 
  onRefresh, 
  onOpenAddItem, 
  onOpenEditItem, 
  onOpenAdjustStock, 
  onOpenUploadDoc, 
  onDeleteItem,
  activeTenant
}) {
  const [searchQuery, setSearchQuery] = useState('');
  const [categoryFilter, setCategoryFilter] = useState('ALL');
  const [stockFilter, setStockFilter] = useState('ALL');

  // Filter items
  const filteredItems = items.filter(item => {
    const matchesSearch = item.name.toLowerCase().includes(searchQuery.toLowerCase()) ||
                          item.sku.toLowerCase().includes(searchQuery.toLowerCase()) ||
                          (item.description && item.description.toLowerCase().includes(searchQuery.toLowerCase()));
    
    const matchesCategory = categoryFilter === 'ALL' || item.category === categoryFilter;
    
    let matchesStock = true;
    if (stockFilter === 'LOW') {
      matchesStock = item.quantity <= item.reorderLevel;
    } else if (stockFilter === 'OUT') {
      matchesStock = item.quantity === 0;
    } else if (stockFilter === 'NORMAL') {
      matchesStock = item.quantity > item.reorderLevel;
    }

    return matchesSearch && matchesCategory && matchesStock;
  });

  const categories = ['ALL', 'Pharmaceuticals', 'Surgical Equipment', 'PPE & Safety', 'Diagnostics', 'Consumables'];

  return (
    <div className="space-y-6 animate-fade-in">
      
      {/* Top Metrics Cards */}
      <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-4 gap-4">
        <div className="glass-panel p-5 relative overflow-hidden group">
          <div className="flex items-center justify-between">
            <div>
              <p className="text-xs font-semibold uppercase text-slate-400">Total SKUs</p>
              <h3 className="text-2xl font-black text-white mt-1">{metrics?.totalSkus || items.length}</h3>
              <p className="text-[11px] text-emerald-400 mt-1 flex items-center gap-1 font-medium">
                <CheckCircle className="w-3 h-3" /> Tenant Isolated Partition
              </p>
            </div>
            <div className="w-12 h-12 rounded-xl bg-blue-500/10 border border-blue-500/20 flex items-center justify-center text-blue-400 group-hover:scale-110 transition-transform">
              <Boxes className="w-6 h-6" />
            </div>
          </div>
        </div>

        <div className="glass-panel p-5 relative overflow-hidden group">
          <div className="flex items-center justify-between">
            <div>
              <p className="text-xs font-semibold uppercase text-slate-400">Total Valuation</p>
              <h3 className="text-2xl font-black text-white mt-1">
                ${(metrics?.totalValuation || items.reduce((acc, i) => acc + (i.quantity * i.unitPrice), 0)).toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 })}
              </h3>
              <p className="text-[11px] text-slate-400 mt-1 font-medium">Real-time inventory value</p>
            </div>
            <div className="w-12 h-12 rounded-xl bg-emerald-500/10 border border-emerald-500/20 flex items-center justify-center text-emerald-400 group-hover:scale-110 transition-transform">
              <DollarSign className="w-6 h-6" />
            </div>
          </div>
        </div>

        <div className="glass-panel p-5 relative overflow-hidden group">
          <div className="flex items-center justify-between">
            <div>
              <p className="text-xs font-semibold uppercase text-slate-400">Low Stock Alerts</p>
              <h3 className="text-2xl font-black text-amber-400 mt-1">
                {items.filter(i => i.quantity <= i.reorderLevel).length}
              </h3>
              <p className="text-[11px] text-amber-400/80 mt-1 font-medium">Below reorder threshold</p>
            </div>
            <div className="w-12 h-12 rounded-xl bg-amber-500/10 border border-amber-500/20 flex items-center justify-center text-amber-400 group-hover:scale-110 transition-transform">
              <AlertTriangle className="w-6 h-6" />
            </div>
          </div>
        </div>

        <div className="glass-panel p-5 relative overflow-hidden group">
          <div className="flex items-center justify-between">
            <div>
              <p className="text-xs font-semibold uppercase text-slate-400">S3 Stored Documents</p>
              <h3 className="text-2xl font-black text-cyan-400 mt-1">
                {items.filter(i => i.s3FileKey).length}
              </h3>
              <p className="text-[11px] text-cyan-400/80 mt-1 font-medium">Encrypted tenant files</p>
            </div>
            <div className="w-12 h-12 rounded-xl bg-cyan-500/10 border border-cyan-500/20 flex items-center justify-center text-cyan-400 group-hover:scale-110 transition-transform">
              <UploadCloud className="w-6 h-6" />
            </div>
          </div>
        </div>
      </div>

      {/* Action & Filter Toolbar */}
      <div className="glass-panel p-4 flex flex-col md:flex-row items-center justify-between gap-4">
        <div className="flex flex-wrap items-center gap-3 w-full md:w-auto">
          {/* Search Bar */}
          <div className="relative flex-1 md:w-72">
            <Search className="w-4 h-4 text-slate-400 absolute left-3 top-1/2 -translate-y-1/2" />
            <input
              type="text"
              placeholder="Search SKU, name, details..."
              value={searchQuery}
              onChange={(e) => setSearchQuery(e.target.value)}
              className="glass-input w-full pl-9"
            />
          </div>

          {/* Category Dropdown */}
          <select
            value={categoryFilter}
            onChange={(e) => setCategoryFilter(e.target.value)}
            className="glass-input cursor-pointer text-xs"
          >
            {categories.map((c) => (
              <option key={c} value={c} className="bg-slate-900 text-white">
                {c === 'ALL' ? 'All Categories' : c}
              </option>
            ))}
          </select>

          {/* Stock Status Dropdown */}
          <select
            value={stockFilter}
            onChange={(e) => setStockFilter(e.target.value)}
            className="glass-input cursor-pointer text-xs"
          >
            <option value="ALL" className="bg-slate-900">All Stock Statuses</option>
            <option value="NORMAL" className="bg-slate-900">Healthy Stock</option>
            <option value="LOW" className="bg-slate-900">Low Stock Alert</option>
            <option value="OUT" className="bg-slate-900">Out of Stock</option>
          </select>
        </div>

        <div className="flex items-center gap-2.5 w-full md:w-auto justify-end">
          <button 
            onClick={onRefresh}
            className="btn-secondary text-xs"
            title="Reload from API"
          >
            <RefreshCw className={`w-3.5 h-3.5 ${loading ? 'animate-spin' : ''}`} />
            <span>Sync</span>
          </button>

          <button 
            onClick={onOpenAddItem}
            className="btn-primary text-xs"
          >
            <Plus className="w-4 h-4" />
            <span>Add Inventory Item</span>
          </button>
        </div>
      </div>

      {/* Inventory Items Table */}
      <div className="glass-panel overflow-hidden">
        <div className="px-6 py-4 border-b border-white/5 flex items-center justify-between bg-slate-900/40">
          <div className="flex items-center space-x-2">
            <h2 className="text-sm font-bold text-white tracking-wide uppercase">
              {activeTenant ? `${activeTenant.name}'s Inventory` : 'Tenant Inventory'}
            </h2>
            <span className="badge badge-blue text-[11px]">{filteredItems.length} items</span>
          </div>

          <div className="text-xs text-slate-400 font-mono">
            Isolated Partition: <span className="text-blue-400">{activeTenant?.code}</span>
          </div>
        </div>

        {loading ? (
          <div className="py-16 text-center text-slate-400">
            <RefreshCw className="w-8 h-8 animate-spin mx-auto text-blue-500 mb-3" />
            <p className="text-sm">Fetching tenant records through EF Core Query Filter...</p>
          </div>
        ) : filteredItems.length === 0 ? (
          <div className="py-16 text-center text-slate-400">
            <Package className="w-12 h-12 mx-auto text-slate-600 mb-3" />
            <h4 className="text-base font-semibold text-slate-300">No items found</h4>
            <p className="text-xs text-slate-500 mt-1 max-w-sm mx-auto">
              No inventory records exist for this tenant matching the selected filters.
            </p>
            <button 
              onClick={onOpenAddItem}
              className="btn-primary mt-4 text-xs"
            >
              <Plus className="w-3.5 h-3.5" />
              <span>Create First Item</span>
            </button>
          </div>
        ) : (
          <div className="overflow-x-auto">
            <table className="custom-table">
              <thead>
                <tr>
                  <th>Item / SKU</th>
                  <th>Category</th>
                  <th>Stock Level</th>
                  <th>Unit Price</th>
                  <th>Total Value</th>
                  <th>S3 Document</th>
                  <th className="text-right">Actions</th>
                </tr>
              </thead>
              <tbody>
                {filteredItems.map((item) => {
                  const isLow = item.quantity <= item.reorderLevel && item.quantity > 0;
                  const isOut = item.quantity === 0;
                  const stockPercent = Math.min(100, Math.round((item.quantity / (item.reorderLevel * 3 || 100)) * 100));

                  return (
                    <tr key={item.id} className="group">
                      <td>
                        <div className="font-semibold text-white group-hover:text-blue-400 transition-colors">
                          {item.name}
                        </div>
                        <div className="text-[11px] font-mono text-slate-400 flex items-center gap-2 mt-0.5">
                          <span>SKU: {item.sku}</span>
                        </div>
                      </td>

                      <td>
                        <span className="badge badge-purple text-[11px]">
                          {item.category}
                        </span>
                      </td>

                      <td>
                        <div className="space-y-1">
                          <div className="flex items-center space-x-2">
                            <span className="font-bold text-white text-sm">{item.quantity}</span>
                            <span className="text-[11px] text-slate-400">/ min {item.reorderLevel}</span>
                            
                            {isOut ? (
                              <span className="badge badge-rose text-[10px]">Out of Stock</span>
                            ) : isLow ? (
                              <span className="badge badge-amber text-[10px]">Low Stock</span>
                            ) : (
                              <span className="badge badge-emerald text-[10px]">Healthy</span>
                            )}
                          </div>
                          {/* Mini Progress Bar */}
                          <div className="w-28 h-1.5 bg-slate-800 rounded-full overflow-hidden">
                            <div 
                              className={`h-full rounded-full ${
                                isOut ? 'bg-rose-500' : isLow ? 'bg-amber-500' : 'bg-emerald-500'
                              }`}
                              style={{ width: `${Math.max(5, stockPercent)}%` }}
                            ></div>
                          </div>
                        </div>
                      </td>

                      <td>
                        <span className="font-mono text-white text-sm font-semibold">
                          ${item.unitPrice.toFixed(2)}
                        </span>
                      </td>

                      <td>
                        <span className="font-mono text-slate-300 text-sm">
                          ${(item.quantity * item.unitPrice).toFixed(2)}
                        </span>
                      </td>

                      <td>
                        {item.s3FileKey ? (
                          <div className="flex items-center space-x-1.5 text-xs text-cyan-400">
                            <FileText className="w-3.5 h-3.5" />
                            <span className="truncate max-w-[120px] text-[11px] font-mono" title={item.s3FileKey}>
                              {item.s3FileKey.split('/').pop()}
                            </span>
                          </div>
                        ) : (
                          <button
                            onClick={() => onOpenUploadDoc(item)}
                            className="text-[11px] text-slate-400 hover:text-cyan-400 flex items-center space-x-1 transition"
                          >
                            <UploadCloud className="w-3 h-3" />
                            <span>Upload Spec</span>
                          </button>
                        )}
                      </td>

                      <td className="text-right">
                        <div className="flex items-center justify-end space-x-1.5">
                          <button
                            onClick={() => onOpenAdjustStock(item)}
                            className="p-1.5 rounded-lg bg-blue-500/10 hover:bg-blue-500/20 text-blue-400 border border-blue-500/30 transition text-xs flex items-center gap-1"
                            title="Adjust Stock In/Out"
                          >
                            <ArrowUpDown className="w-3.5 h-3.5" />
                            <span className="hidden sm:inline">Stock</span>
                          </button>

                          <button
                            onClick={() => onOpenUploadDoc(item)}
                            className="p-1.5 rounded-lg bg-slate-800 hover:bg-slate-700 text-cyan-400 border border-white/5 transition"
                            title="Attach S3 Spec Sheet / Image"
                          >
                            <UploadCloud className="w-3.5 h-3.5" />
                          </button>

                          <button
                            onClick={() => onOpenEditItem(item)}
                            className="p-1.5 rounded-lg bg-slate-800 hover:bg-slate-700 text-slate-300 border border-white/5 transition"
                            title="Edit Item"
                          >
                            <Edit3 className="w-3.5 h-3.5" />
                          </button>

                          <button
                            onClick={() => onDeleteItem(item.id)}
                            className="p-1.5 rounded-lg bg-rose-500/10 hover:bg-rose-500/20 text-rose-400 border border-rose-500/20 transition"
                            title="Delete Item"
                          >
                            <Trash2 className="w-3.5 h-3.5" />
                          </button>
                        </div>
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
        )}
      </div>

    </div>
  );
}
