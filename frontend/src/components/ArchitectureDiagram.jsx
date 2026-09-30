import React from 'react';
import { 
  ShieldCheck, 
  Server, 
  Database, 
  HardDrive, 
  Layers, 
  ArrowRight, 
  Lock, 
  CheckCircle,
  FileCode,
  Globe
} from 'lucide-react';

export default function ArchitectureDiagram({ activeTenant }) {
  return (
    <div className="space-y-6 animate-fade-in">
      
      {/* Title */}
      <div className="glass-panel p-6 border-blue-500/30 bg-gradient-to-r from-blue-950/40 via-slate-900/60 to-slate-900/60">
        <div className="flex flex-col md:flex-row items-start md:items-center justify-between gap-4">
          <div>
            <div className="flex items-center space-x-2">
              <span className="badge badge-blue">Interactive Architecture</span>
              <span className="badge badge-purple">EF Core 8.0 & AWS S3</span>
            </div>
            <h2 className="text-xl font-black text-white mt-1">
              Multi-Tenant Request Lifecycle & Isolation Pipeline
            </h2>
            <p className="text-xs text-slate-300 max-w-2xl mt-1">
              Trace how incoming client requests carrying the active tenant header 
              <code className="text-cyan-300 font-mono ml-1 font-bold">X-Tenant-ID: {activeTenant?.code}</code> 
              are processed, filtered, and safeguarded against cross-tenant data leaks.
            </p>
          </div>
        </div>
      </div>

      {/* Visual Pipeline Grid */}
      <div className="grid grid-cols-1 md:grid-cols-4 gap-4">
        
        {/* Step 1 */}
        <div className="glass-panel p-5 relative border-cyan-500/20 space-y-3">
          <div className="flex items-center justify-between">
            <span className="w-7 h-7 rounded-full bg-cyan-500/20 text-cyan-400 font-bold text-xs flex items-center justify-center border border-cyan-500/40">1</span>
            <Globe className="w-5 h-5 text-cyan-400" />
          </div>
          <h4 className="text-sm font-bold text-white">Client Request Header</h4>
          <p className="text-xs text-slate-400 leading-relaxed">
            Frontend attaches HTTP Header with every API request:
          </p>
          <div className="p-2.5 bg-slate-950 rounded-lg text-[11px] font-mono text-cyan-300 border border-white/5 overflow-x-auto">
            X-Tenant-ID: {activeTenant?.id}<br/>
            X-Tenant-Code: {activeTenant?.code}
          </div>
        </div>

        {/* Step 2 */}
        <div className="glass-panel p-5 relative border-indigo-500/20 space-y-3">
          <div className="flex items-center justify-between">
            <span className="w-7 h-7 rounded-full bg-indigo-500/20 text-indigo-400 font-bold text-xs flex items-center justify-center border border-indigo-500/40">2</span>
            <Server className="w-5 h-5 text-indigo-400" />
          </div>
          <h4 className="text-sm font-bold text-white">Tenant Middleware</h4>
          <p className="text-xs text-slate-400 leading-relaxed">
            <code>TenantResolutionMiddleware</code> validates tenant identity and registers scoped <code>ITenantService</code>:
          </p>
          <div className="p-2.5 bg-slate-950 rounded-lg text-[11px] font-mono text-indigo-300 border border-white/5">
            tenantService.SetTenant({'{'}<br/>
            &nbsp;&nbsp;Code: "{activeTenant?.code}",<br/>
            &nbsp;&nbsp;Name: "{activeTenant?.name}"<br/>
            {'}'})
          </div>
        </div>

        {/* Step 3 */}
        <div className="glass-panel p-5 relative border-purple-500/20 space-y-3">
          <div className="flex items-center justify-between">
            <span className="w-7 h-7 rounded-full bg-purple-500/20 text-purple-400 font-bold text-xs flex items-center justify-center border border-purple-500/40">3</span>
            <Database className="w-5 h-5 text-purple-400" />
          </div>
          <h4 className="text-sm font-bold text-white">EF Core Query Filter</h4>
          <p className="text-xs text-slate-400 leading-relaxed">
            Global Query Filter automatically attaches to all SQL operations:
          </p>
          <div className="p-2.5 bg-slate-950 rounded-lg text-[11px] font-mono text-purple-300 border border-white/5">
            WHERE [t].[TenantId] = @__CurrentTenantId
          </div>
        </div>

        {/* Step 4 */}
        <div className="glass-panel p-5 relative border-emerald-500/20 space-y-3">
          <div className="flex items-center justify-between">
            <span className="w-7 h-7 rounded-full bg-emerald-500/20 text-emerald-400 font-bold text-xs flex items-center justify-center border border-emerald-500/40">4</span>
            <HardDrive className="w-5 h-5 text-emerald-400" />
          </div>
          <h4 className="text-sm font-bold text-white">S3 Partition Vault</h4>
          <p className="text-xs text-slate-400 leading-relaxed">
            AWS S3 isolated storage path hierarchy prevents cross-tenant traversal:
          </p>
          <div className="p-2.5 bg-slate-950 rounded-lg text-[11px] font-mono text-emerald-300 border border-white/5 truncate" title={`s3://bucket/${activeTenant?.code}/*`}>
            s3://bucket/{activeTenant?.code}/*
          </div>
        </div>

      </div>

      {/* Code Snippet Cards */}
      <div className="grid grid-cols-1 md:grid-cols-2 gap-6">
        <div className="glass-panel p-5 space-y-3">
          <div className="flex items-center justify-between">
            <h4 className="text-xs font-bold uppercase tracking-wider text-slate-300 flex items-center gap-2">
              <FileCode className="w-4 h-4 text-blue-400" />
              <span>EF Core Global Filter Implementation (C#)</span>
            </h4>
            <span className="badge badge-blue text-[10px]">AppDbContext.cs</span>
          </div>

          <pre className="p-3 bg-slate-950 rounded-xl text-xs font-mono text-slate-300 border border-white/5 overflow-x-auto">
{`modelBuilder.Entity<InventoryItem>()
    .HasQueryFilter(e => _tenantService.CurrentTenantId == null 
                      || e.TenantId == _tenantService.CurrentTenantId);

// Automatic Tenant ID injection on SaveChangesAsync:
foreach (var entry in ChangeTracker.Entries<IMultiTenant>()) {
    if (entry.State == EntityState.Added) {
        entry.Entity.TenantId = _tenantService.CurrentTenantId;
    }
}`}
          </pre>
        </div>

        <div className="glass-panel p-5 space-y-3">
          <div className="flex items-center justify-between">
            <h4 className="text-xs font-bold uppercase tracking-wider text-slate-300 flex items-center gap-2">
              <Lock className="w-4 h-4 text-emerald-400" />
              <span>S3 Tenant Path Guard (C#)</span>
            </h4>
            <span className="badge badge-emerald text-[10px]">S3StorageService.cs</span>
          </div>

          <pre className="p-3 bg-slate-950 rounded-xl text-xs font-mono text-slate-300 border border-white/5 overflow-x-auto">
{`public Task<string> GetPresignedDownloadUrlAsync(string tenantCode, string key) {
    if (!key.StartsWith($"\${tenantCode}/")) {
        throw new UnauthorizedAccessException(
            "Access denied to foreign tenant S3 partition.");
    }
    return GeneratePresignedUrl(key);
}`}
          </pre>
        </div>
      </div>

    </div>
  );
}
