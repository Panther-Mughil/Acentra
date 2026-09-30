import React, { useState } from 'react';
import { X, Building2, Plus, Globe, Key, Shield } from 'lucide-react';

export default function CreateTenantModal({ isOpen, onClose, onCreateTenant }) {
  const [name, setName] = useState('');
  const [code, setCode] = useState('');
  const [tier, setTier] = useState('Enterprise');
  const [submitting, setSubmitting] = useState(false);

  if (!isOpen) return null;

  const handleNameChange = (val) => {
    setName(val);
    if (!code || code === name.toLowerCase().replace(/[^a-z0-9]/g, '-')) {
      setCode(val.toLowerCase().replace(/[^a-z0-9]/g, '-'));
    }
  };

  const handleSubmit = async (e) => {
    e.preventDefault();
    if (!name || !code) return;
    setSubmitting(true);
    try {
      await onCreateTenant({
        name,
        code,
        subscriptionTier: tier
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
      <div className="modal-content p-6 max-w-md w-full">
        <div className="flex items-center justify-between pb-4 border-b border-white/10">
          <div className="flex items-center space-x-2">
            <Building2 className="w-5 h-5 text-indigo-400" />
            <h3 className="text-base font-bold text-white">Register New Tenant</h3>
          </div>
          <button onClick={onClose} className="p-1 rounded-lg hover:bg-slate-800 text-slate-400 hover:text-white">
            <X className="w-5 h-5" />
          </button>
        </div>

        <form onSubmit={handleSubmit} className="mt-4 space-y-4">
          <div>
            <label className="block text-xs font-semibold uppercase text-slate-400 mb-1">
              Organization / Tenant Name *
            </label>
            <input
              type="text"
              required
              value={name}
              onChange={(e) => handleNameChange(e.target.value)}
              className="glass-input w-full"
              placeholder="e.g. Apex Health Systems"
            />
          </div>

          <div>
            <label className="block text-xs font-semibold uppercase text-slate-400 mb-1">
              Tenant Code / Subdomain Key *
            </label>
            <div className="relative">
              <Key className="w-4 h-4 text-slate-400 absolute left-3 top-1/2 -translate-y-1/2" />
              <input
                type="text"
                required
                value={code}
                onChange={(e) => setCode(e.target.value.toLowerCase().replace(/[^a-z0-9-]/g, ''))}
                className="glass-input w-full pl-9 font-mono"
                placeholder="e.g. apex-health"
              />
            </div>
            <p className="text-[11px] text-slate-500 mt-1">
              Used for Header <code>X-Tenant-ID</code> & S3 isolation partition
            </p>
          </div>

          <div>
            <label className="block text-xs font-semibold uppercase text-slate-400 mb-1">
              Subscription Tier
            </label>
            <select
              value={tier}
              onChange={(e) => setTier(e.target.value)}
              className="glass-input w-full cursor-pointer"
            >
              <option value="Enterprise" className="bg-slate-900">Enterprise HIPAA Plan</option>
              <option value="Professional" className="bg-slate-900">Professional Healthcare</option>
              <option value="Starter" className="bg-slate-900">Starter Lab</option>
            </select>
          </div>

          <div className="p-3 bg-indigo-950/30 border border-indigo-500/20 rounded-xl text-xs text-indigo-300 flex items-center space-x-2">
            <Shield className="w-4 h-4 text-indigo-400 flex-shrink-0" />
            <span>Automatic DB seeding and S3 bucket partition created upon registration.</span>
          </div>

          <div className="flex items-center justify-end space-x-3 pt-3 border-t border-white/10">
            <button type="button" onClick={onClose} className="btn-secondary text-xs">
              Cancel
            </button>
            <button type="submit" disabled={submitting} className="btn-primary text-xs">
              {submitting ? 'Creating Tenant...' : 'Provision Isolated Tenant'}
            </button>
          </div>
        </form>
      </div>
    </div>
  );
}
