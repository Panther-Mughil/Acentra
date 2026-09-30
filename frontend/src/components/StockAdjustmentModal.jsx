import React, { useState } from 'react';
import { X, ArrowUpDown, PlusCircle, MinusCircle, AlertCircle } from 'lucide-react';

export default function StockAdjustmentModal({ item, isOpen, onClose, onAdjust }) {
  const [type, setType] = useState('INBOUND'); // INBOUND, OUTBOUND, AUDIT_CORRECTION
  const [quantity, setQuantity] = useState(10);
  const [reason, setReason] = useState('');
  const [submitting, setSubmitting] = useState(false);

  if (!isOpen || !item) return null;

  const handleSubmit = async (e) => {
    e.preventDefault();
    if (quantity <= 0) return;
    setSubmitting(true);
    try {
      await onAdjust(item.id, {
        type,
        quantity: parseInt(quantity, 10),
        reason: reason || `${type} adjustment`
      });
      onClose();
    } catch (err) {
      console.error(err);
    } finally {
      setSubmitting(false);
    }
  };

  const calculatedNewStock = () => {
    const qty = parseInt(quantity, 10) || 0;
    if (type === 'INBOUND') return item.quantity + qty;
    if (type === 'OUTBOUND') return Math.max(0, item.quantity - qty);
    if (type === 'AUDIT_CORRECTION') return qty;
    return item.quantity;
  };

  return (
    <div className="modal-overlay">
      <div className="modal-content p-6 max-w-md w-full">
        <div className="flex items-center justify-between pb-4 border-b border-white/10">
          <div className="flex items-center space-x-2">
            <ArrowUpDown className="w-5 h-5 text-blue-400" />
            <h3 className="text-base font-bold text-white">Stock Adjustment</h3>
          </div>
          <button onClick={onClose} className="p-1 rounded-lg hover:bg-slate-800 text-slate-400 hover:text-white">
            <X className="w-5 h-5" />
          </button>
        </div>

        <form onSubmit={handleSubmit} className="mt-4 space-y-4">
          <div className="p-3 bg-slate-950/60 rounded-xl border border-white/5">
            <p className="text-xs text-slate-400">Target SKU / Item:</p>
            <div className="text-sm font-bold text-white">{item.name}</div>
            <div className="text-xs font-mono text-slate-400">SKU: {item.sku} | Current Stock: <strong className="text-emerald-400">{item.quantity}</strong></div>
          </div>

          <div>
            <label className="block text-xs font-semibold uppercase text-slate-400 mb-1">Adjustment Type</label>
            <div className="grid grid-cols-3 gap-2">
              <button
                type="button"
                onClick={() => setType('INBOUND')}
                className={`py-2 px-3 rounded-lg text-xs font-semibold flex items-center justify-center space-x-1 border transition ${
                  type === 'INBOUND' ? 'bg-emerald-600/20 border-emerald-500 text-emerald-400' : 'border-white/10 text-slate-400 hover:bg-slate-800'
                }`}
              >
                <PlusCircle className="w-3.5 h-3.5" />
                <span>Inbound</span>
              </button>

              <button
                type="button"
                onClick={() => setType('OUTBOUND')}
                className={`py-2 px-3 rounded-lg text-xs font-semibold flex items-center justify-center space-x-1 border transition ${
                  type === 'OUTBOUND' ? 'bg-rose-600/20 border-rose-500 text-rose-400' : 'border-white/10 text-slate-400 hover:bg-slate-800'
                }`}
              >
                <MinusCircle className="w-3.5 h-3.5" />
                <span>Outbound</span>
              </button>

              <button
                type="button"
                onClick={() => setType('AUDIT_CORRECTION')}
                className={`py-2 px-3 rounded-lg text-xs font-semibold flex items-center justify-center space-x-1 border transition ${
                  type === 'AUDIT_CORRECTION' ? 'bg-blue-600/20 border-blue-500 text-blue-400' : 'border-white/10 text-slate-400 hover:bg-slate-800'
                }`}
              >
                <span>Audit Set</span>
              </button>
            </div>
          </div>

          <div>
            <label className="block text-xs font-semibold uppercase text-slate-400 mb-1">
              {type === 'AUDIT_CORRECTION' ? 'New Exact Stock Level' : 'Quantity to Adjust'}
            </label>
            <input
              type="number"
              min="1"
              required
              value={quantity}
              onChange={(e) => setQuantity(e.target.value)}
              className="glass-input w-full font-mono text-base"
            />
          </div>

          <div className="p-3 bg-blue-950/30 rounded-xl border border-blue-500/20 text-xs text-blue-300 flex items-center justify-between">
            <span>Projected Resulting Stock:</span>
            <span className="text-base font-bold font-mono text-white">{calculatedNewStock()}</span>
          </div>

          <div>
            <label className="block text-xs font-semibold uppercase text-slate-400 mb-1">Audit Reason / Order Ref</label>
            <input
              type="text"
              placeholder="e.g. PO #8921 Restock, Emergency Ward requisition"
              value={reason}
              onChange={(e) => setReason(e.target.value)}
              className="glass-input w-full"
            />
          </div>

          <div className="flex items-center justify-end space-x-3 pt-3 border-t border-white/10">
            <button type="button" onClick={onClose} className="btn-secondary text-xs">Cancel</button>
            <button type="submit" disabled={submitting} className="btn-primary text-xs">
              {submitting ? 'Applying...' : 'Confirm Stock Adjustment'}
            </button>
          </div>
        </form>
      </div>
    </div>
  );
}
