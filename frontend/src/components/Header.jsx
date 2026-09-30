import React, { useState } from 'react';
import { 
  Building2, 
  ShieldCheck, 
  ChevronDown, 
  Plus, 
  Layers, 
  Database, 
  HardDrive,
  CheckCircle2,
  Lock,
  Sparkles,
  GitBranch,
  Network
} from 'lucide-react';

export default function Header({ 
  tenants, 
  activeTenant, 
  onSelectTenant, 
  onOpenCreateTenant, 
  activeTab, 
  setActiveTab 
}) {
  const [dropdownOpen, setDropdownOpen] = useState(false);

  return (
    <header className="border-b border-[rgba(255,255,255,0.08)] bg-[#0f172a]/90 backdrop-blur-xl sticky top-0 z-40">
      <div className="max-w-7xl mx-auto px-4 sm:px-6 lg:px-8">
        <div className="flex items-center justify-between h-16">
          
          {/* Logo & Brand */}
          <div className="flex items-center space-x-3">
            <div className="w-10 h-10 rounded-xl bg-gradient-to-tr from-blue-600 via-indigo-600 to-cyan-400 flex items-center justify-center shadow-lg shadow-blue-500/20">
              <ShieldCheck className="w-6 h-6 text-white" />
            </div>
            <div>
              <div className="flex items-center space-x-2">
                <span className="text-lg font-bold tracking-tight text-white">Acentra Multi-Tenant</span>
                <span className="badge badge-blue text-[10px] uppercase font-mono tracking-wider">EF-Core 8.0</span>
              </div>
              <p className="text-xs text-slate-400">Multi-Tenant Inventory & AWS S3 Isolated Partitions</p>
            </div>
          </div>

          {/* Navigation Tabs */}
          <div className="hidden lg:flex items-center space-x-1 bg-slate-900/80 p-1 rounded-xl border border-white/5">
            <button
              onClick={() => setActiveTab('inventory')}
              className={`px-3.5 py-1.5 rounded-lg text-xs font-semibold flex items-center space-x-2 transition-all ${
                activeTab === 'inventory' 
                  ? 'bg-blue-600 text-white shadow-md shadow-blue-600/30' 
                  : 'text-slate-400 hover:text-slate-200 hover:bg-white/5'
              }`}
            >
              <Layers className="w-3.5 h-3.5" />
              <span>Inventory Dashboard</span>
            </button>

            <button
              onClick={() => setActiveTab('s3storage')}
              className={`px-3.5 py-1.5 rounded-lg text-xs font-semibold flex items-center space-x-2 transition-all ${
                activeTab === 's3storage' 
                  ? 'bg-blue-600 text-white shadow-md shadow-blue-600/30' 
                  : 'text-slate-400 hover:text-slate-200 hover:bg-white/5'
              }`}
            >
              <HardDrive className="w-3.5 h-3.5" />
              <span>S3 Vault</span>
            </button>

            <button
              onClick={() => setActiveTab('architecture')}
              className={`px-3.5 py-1.5 rounded-lg text-xs font-semibold flex items-center space-x-2 transition-all ${
                activeTab === 'architecture' 
                  ? 'bg-indigo-600 text-white shadow-md shadow-indigo-600/30' 
                  : 'text-slate-400 hover:text-slate-200 hover:bg-white/5'
              }`}
            >
              <Network className="w-3.5 h-3.5 text-indigo-300" />
              <span>Pipeline & Architecture</span>
            </button>

            <button
              onClick={() => setActiveTab('audit')}
              className={`px-3.5 py-1.5 rounded-lg text-xs font-semibold flex items-center space-x-2 transition-all ${
                activeTab === 'audit' 
                  ? 'bg-purple-600 text-white shadow-md shadow-purple-600/30' 
                  : 'text-slate-400 hover:text-slate-200 hover:bg-white/5'
              }`}
            >
              <Lock className="w-3.5 h-3.5 text-purple-400" />
              <span>Jury Leak Probe</span>
              <span className="w-2 h-2 rounded-full bg-purple-400 animate-pulse"></span>
            </button>
          </div>

          {/* Quick Switcher & Active Tenant Dropdown */}
          <div className="flex items-center space-x-3">
            
            {/* Quick Tenant Switcher Pills (Top Bar) */}
            <div className="hidden sm:flex items-center space-x-1.5 bg-slate-950/60 p-1 rounded-xl border border-white/5">
              {tenants.map((t) => {
                const isSelected = activeTenant && activeTenant.id === t.id;
                return (
                  <button
                    key={t.id}
                    onClick={() => onSelectTenant(t)}
                    className={`px-2.5 py-1 rounded-lg text-[11px] font-semibold transition-all flex items-center space-x-1.5 ${
                      isSelected
                        ? 'bg-blue-600/30 border border-blue-500/50 text-white shadow-sm'
                        : 'text-slate-400 hover:text-slate-200 hover:bg-white/5'
                    }`}
                  >
                    <span className={`w-1.5 h-1.5 rounded-full ${isSelected ? 'bg-emerald-400 animate-pulse' : 'bg-slate-500'}`}></span>
                    <span>{t.code}</span>
                  </button>
                );
              })}
            </div>

            {/* Tenant Selector Dropdown */}
            <div className="relative">
              <button
                onClick={() => setDropdownOpen(!dropdownOpen)}
                className="flex items-center space-x-2.5 px-3 py-1.5 rounded-xl bg-slate-800 hover:bg-slate-700/80 border border-white/10 text-white transition-all shadow-sm group"
              >
                <div className="w-6 h-6 rounded-lg bg-gradient-to-br from-indigo-500 to-purple-600 flex items-center justify-center text-xs font-bold uppercase">
                  {activeTenant ? activeTenant.name.charAt(0) : '?'}
                </div>
                <div className="text-left">
                  <div className="text-xs font-bold leading-none text-slate-200 group-hover:text-blue-400 max-w-[130px] truncate">
                    {activeTenant ? activeTenant.name : 'Select Tenant'}
                  </div>
                </div>
                <ChevronDown className="w-3.5 h-3.5 text-slate-400" />
              </button>

              {dropdownOpen && (
                <div 
                  className="absolute right-0 mt-2 w-72 rounded-2xl bg-slate-900 border border-slate-700/80 shadow-2xl p-2 z-50 animate-fade-in"
                  onMouseLeave={() => setDropdownOpen(false)}
                >
                  <div className="px-3 py-2 border-b border-white/5 mb-1">
                    <span className="text-[10px] font-bold uppercase text-slate-400 tracking-wider">
                      Switch Active Organization Context
                    </span>
                    <p className="text-[11px] text-slate-500 font-mono mt-0.5">
                      Sends <code className="text-blue-400">X-Tenant-ID</code>
                    </p>
                  </div>

                  <div className="space-y-1 max-h-60 overflow-y-auto">
                    {tenants.map((tenant) => {
                      const isSelected = activeTenant && activeTenant.id === tenant.id;
                      return (
                        <button
                          key={tenant.id}
                          onClick={() => {
                            onSelectTenant(tenant);
                            setDropdownOpen(false);
                          }}
                          className={`w-full text-left px-3 py-2.5 rounded-xl flex items-center justify-between transition-all ${
                            isSelected 
                              ? 'bg-blue-600/20 border border-blue-500/40 text-white' 
                              : 'hover:bg-slate-800/60 text-slate-300'
                          }`}
                        >
                          <div className="flex items-center space-x-2.5">
                            <Building2 className={`w-4 h-4 ${isSelected ? 'text-blue-400' : 'text-slate-400'}`} />
                            <div>
                              <div className="text-xs font-semibold">{tenant.name}</div>
                              <div className="text-[10px] text-slate-400 font-mono">Code: {tenant.code}</div>
                            </div>
                          </div>
                          {isSelected && <CheckCircle2 className="w-4 h-4 text-blue-400" />}
                        </button>
                      );
                    })}
                  </div>

                  <div className="border-t border-white/5 pt-2 mt-2">
                    <button
                      onClick={() => {
                        setDropdownOpen(false);
                        onOpenCreateTenant();
                      }}
                      className="w-full py-2 px-3 rounded-xl bg-slate-800 hover:bg-slate-700 text-slate-200 text-xs font-medium flex items-center justify-center space-x-2 transition"
                    >
                      <Plus className="w-3.5 h-3.5 text-blue-400" />
                      <span>Register New Tenant</span>
                    </button>
                  </div>
                </div>
              )}
            </div>

          </div>

        </div>
      </div>

      {/* Tenant Context Real-Time Banner */}
      <div className="bg-slate-950/70 border-t border-b border-white/5 py-1 px-4 text-xs font-mono">
        <div className="max-w-7xl mx-auto flex flex-wrap items-center justify-between gap-2 text-slate-400 text-[11px]">
          <div className="flex items-center space-x-3">
            <span className="flex items-center text-emerald-400 gap-1.5 font-semibold">
              <span className="w-2 h-2 rounded-full bg-emerald-400 animate-ping"></span>
              Tenant Isolation Enforced
            </span>
            <span>|</span>
            <span>Active Tenant: <strong className="text-white">{activeTenant?.name || 'None'}</strong></span>
            <span>|</span>
            <span>Header: <code className="text-blue-400 font-bold">X-Tenant-ID: {activeTenant?.code || 'null'}</code></span>
          </div>

          <div className="flex items-center space-x-3">
            <span className="badge badge-purple text-[10px]">EF Core Query Filter: Active</span>
            <span className="badge badge-blue text-[10px]">S3 Prefix: s3://vault/{activeTenant?.code || 'tenant'}/</span>
          </div>
        </div>
      </div>
    </header>
  );
}
