import React, { useState, useEffect } from 'react';
import { X, PackagePlus, Edit3, DollarSign, Tag, Hash, FileText } from 'lucide-react';

export default function ItemModal({ isOpen, onClose, onSave, itemToEdit, activeTenant }) {
  const [formData, setFormData] = useState({
    sku: '',
    name: '',
    category: 'Pharmaceuticals',
    quantity: 100,
    unitPrice: 19.99,
    reorderLevel: 25,
    description: '',
  });
  const [submitting, setSubmitting] = useState(false);

  useEffect(() => {
    if (itemToEdit) {
      setFormData({
        sku: itemToEdit.sku || '',
        name: itemToEdit.name || '',
        category: itemToEdit.category || 'Pharmaceuticals',
        quantity: itemToEdit.quantity || 0,
        unitPrice: itemToEdit.unitPrice || 0,
        reorderLevel: itemToEdit.reorderLevel || 20,
        description: itemToEdit.description || '',
      });
    } else {
      setFormData({
        sku: `MED-${Math.floor(1000 + Math.random() * 9000)}`,
        name: '',
        category: 'Pharmaceuticals',
        quantity: 100,
        unitPrice: 24.50,
        reorderLevel: 20,
        description: '',
      });
    }
  }, [itemToEdit, isOpen]);

  if (!isOpen) return null;

  const categories = ['Pharmaceuticals', 'Surgical Equipment', 'PPE & Safety', 'Diagnostics', 'Consumables'];

  const handleSubmit = async (e) => {
    e.preventDefault();
    setSubmitting(true);
    try {
      await onSave({
        ...formData,
        quantity: parseInt(formData.quantity, 10),
        unitPrice: parseFloat(formData.unitPrice),
        reorderLevel: parseInt(formData.reorderLevel, 10),
      });
      onClose();
    } catch (err) {
      console.error(err);
    } finally {
      setSubmitting(false);
    }
  };

  return (
    <div className="modal-overlay">
      <div className="modal-content p-6">
        <div className="flex items-center justify-between pb-4 border-b border-white/10">
          <div className="flex items-center space-x-2">
            {itemToEdit ? (
              <Edit3 className="w-5 h-5 text-blue-400" />
            ) : (
              <PackagePlus className="w-5 h-5 text-blue-400" />
            )}
            <h3 className="text-base font-bold text-white">
              {itemToEdit ? 'Edit Inventory Item' : 'Add New Inventory Item'}
            </h3>
          </div>
          <button onClick={onClose} className="p-1 rounded-lg hover:bg-slate-800 text-slate-400 hover:text-white">
            <X className="w-5 h-5" />
          </button>
        </div>

        <div className="mt-3 p-3 bg-blue-950/40 border border-blue-500/20 rounded-xl text-xs text-blue-300">
          Enforcing automatic multi-tenant isolation for: <strong className="text-white">{activeTenant?.name}</strong> (Tenant ID: <code className="font-mono text-cyan-300">{activeTenant?.code}</code>)
        </div>

        <form onSubmit={handleSubmit} className="mt-4 space-y-4">
          <div className="grid grid-cols-1 sm:grid-cols-2 gap-4">
            <div>
              <label className="block text-xs font-semibold uppercase text-slate-400 mb-1">
                SKU Identifier *
              </label>
              <div className="relative">
                <Hash className="w-4 h-4 text-slate-400 absolute left-3 top-1/2 -translate-y-1/2" />
                <input
                  type="text"
                  required
                  value={formData.sku}
                  onChange={(e) => setFormData({ ...formData, sku: e.target.value.toUpperCase() })}
                  className="glass-input w-full pl-9 font-mono"
                  placeholder="e.g. AMX-500MG"
                />
              </div>
            </div>

            <div>
              <label className="block text-xs font-semibold uppercase text-slate-400 mb-1">
                Category *
              </label>
              <select
                value={formData.category}
                onChange={(e) => setFormData({ ...formData, category: e.target.value })}
                className="glass-input w-full cursor-pointer"
              >
                {categories.map((c) => (
                  <option key={c} value={c} className="bg-slate-900 text-white">
                    {c}
                  </option>
                ))}
              </select>
            </div>
          </div>

          <div>
            <label className="block text-xs font-semibold uppercase text-slate-400 mb-1">
              Item Name *
            </label>
            <input
              type="text"
              required
              value={formData.name}
              onChange={(e) => setFormData({ ...formData, name: e.target.value })}
              className="glass-input w-full"
              placeholder="e.g. Amoxicillin Trihydrate 500mg USP Capsules"
            />
          </div>

          <div className="grid grid-cols-1 sm:grid-cols-3 gap-4">
            <div>
              <label className="block text-xs font-semibold uppercase text-slate-400 mb-1">
                Initial Stock Qty *
              </label>
              <input
                type="number"
                min="0"
                required
                value={formData.quantity}
                onChange={(e) => setFormData({ ...formData, quantity: e.target.value })}
                className="glass-input w-full font-mono"
              />
            </div>

            <div>
              <label className="block text-xs font-semibold uppercase text-slate-400 mb-1">
                Unit Price ($) *
              </label>
              <input
                type="number"
                step="0.01"
                min="0"
                required
                value={formData.unitPrice}
                onChange={(e) => setFormData({ ...formData, unitPrice: e.target.value })}
                className="glass-input w-full font-mono"
              />
            </div>

            <div>
              <label className="block text-xs font-semibold uppercase text-slate-400 mb-1">
                Reorder Level *
              </label>
              <input
                type="number"
                min="1"
                required
                value={formData.reorderLevel}
                onChange={(e) => setFormData({ ...formData, reorderLevel: e.target.value })}
                className="glass-input w-full font-mono"
                title="Threshold for Low Stock alert"
              />
            </div>
          </div>

          <div>
            <label className="block text-xs font-semibold uppercase text-slate-400 mb-1">
              Description & Specifications
            </label>
            <textarea
              rows="3"
              value={formData.description}
              onChange={(e) => setFormData({ ...formData, description: e.target.value })}
              className="glass-input w-full resize-none"
              placeholder="Dosage form, storage temperature, manufacturer batch details..."
            ></textarea>
          </div>

          <div className="flex items-center justify-end space-x-3 pt-3 border-t border-white/10">
            <button type="button" onClick={onClose} className="btn-secondary text-xs">
              Cancel
            </button>
            <button type="submit" disabled={submitting} className="btn-primary text-xs">
              {submitting ? 'Saving...' : itemToEdit ? 'Save Changes' : 'Create Inventory Item'}
            </button>
          </div>
        </form>
      </div>
    </div>
  );
}
