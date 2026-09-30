import React, { useState } from 'react';
import { 
  HardDrive, 
  Folder, 
  FileText, 
  Download, 
  Lock, 
  ExternalLink, 
  ShieldCheck, 
  Eye, 
  RefreshCw 
} from 'lucide-react';

export default function S3StorageExplorer({ activeTenant, items }) {
  const [selectedFile, setSelectedFile] = useState(null);

  // Filter items with S3 files attached
  const itemsWithFiles = items.filter(i => i.s3FileKey);

  return (
    <div className="space-y-6 animate-fade-in">
      
      {/* S3 Architecture Banner */}
      <div className="glass-panel p-6 border-cyan-500/30 bg-gradient-to-r from-cyan-950/40 via-slate-900/60 to-slate-900/60">
        <div className="flex flex-col md:flex-row items-start md:items-center justify-between gap-4">
          <div className="space-y-1">
            <div className="flex items-center space-x-2">
              <span className="badge badge-blue">AWS S3 Multi-Tenant Store</span>
              <span className="badge badge-emerald">Prefix Partitioning</span>
            </div>
            <h2 className="text-xl font-black text-white">
              S3 Tenant-Isolated File Storage Explorer
            </h2>
            <p className="text-xs text-slate-300 max-w-2xl">
              Files are strictly saved inside dedicated tenant partitions under AWS S3: 
              <code className="text-cyan-300 ml-1">s3://acentra-inventory-vault/{activeTenant?.code || 'tenant'}/...</code>
            </p>
          </div>

          <div className="flex items-center space-x-2 text-xs font-mono bg-slate-950/80 px-3 py-2 rounded-xl border border-white/10">
            <ShieldCheck className="w-4 h-4 text-emerald-400" />
            <span>IAM Policy: <strong className="text-white">Tenant-Scoped ARN</strong></span>
          </div>
        </div>
      </div>

      {/* S3 Partition Tree & Files */}
      <div className="grid grid-cols-1 md:grid-cols-3 gap-6">
        
        {/* Left: S3 Directory Hierarchy */}
        <div className="glass-panel p-5 space-y-3">
          <h3 className="text-xs font-bold uppercase tracking-wider text-slate-400 flex items-center space-x-2">
            <Folder className="w-4 h-4 text-cyan-400" />
            <span>S3 Bucket Partition Tree</span>
          </h3>

          <div className="font-mono text-xs space-y-2 p-3 bg-slate-950 rounded-xl border border-white/5">
            <div className="text-slate-400 flex items-center gap-1.5">
              <HardDrive className="w-3.5 h-3.5 text-blue-400" />
              <span>s3://acentra-inventory-vault/</span>
            </div>
            
            <div className="pl-4 border-l border-white/10 space-y-2">
              <div className="text-cyan-400 font-bold flex items-center gap-1.5">
                <Folder className="w-3.5 h-3.5" />
                <span>{activeTenant?.code}/ (Active Partition)</span>
              </div>

              <div className="pl-4 border-l border-cyan-500/30 space-y-1.5 text-slate-300">
                <div className="flex items-center gap-1.5">
                  <Folder className="w-3 h-3 text-slate-500" />
                  <span>docs/ (Spec Sheets & MSDS)</span>
                </div>
                <div className="flex items-center gap-1.5">
                  <Folder className="w-3 h-3 text-slate-500" />
                  <span>media/ (High-Res Packaging)</span>
                </div>
              </div>

              <div className="text-slate-600 flex items-center gap-1.5 line-through">
                <Lock className="w-3 h-3 text-rose-500" />
                <span>other-tenants/ (ACCESS DENIED)</span>
              </div>
            </div>
          </div>

          <div className="p-3 bg-slate-900/60 rounded-xl text-[11px] text-slate-400 space-y-1">
            <span className="font-semibold text-slate-300 block">S3 Security Guarantee:</span>
            <p>
              Pre-signed URLs are generated dynamically with a 15-minute expiration and verified against the current tenant context.
            </p>
          </div>
        </div>

        {/* Right: S3 Files List */}
        <div className="md:col-span-2 glass-panel p-5 space-y-4">
          <div className="flex items-center justify-between">
            <h3 className="text-xs font-bold uppercase tracking-wider text-slate-400 flex items-center space-x-2">
              <FileText className="w-4 h-4 text-blue-400" />
              <span>Tenant Uploaded S3 Objects ({itemsWithFiles.length})</span>
            </h3>
          </div>

          {itemsWithFiles.length === 0 ? (
            <div className="py-12 text-center text-slate-500 text-xs space-y-2">
              <HardDrive className="w-8 h-8 mx-auto text-slate-600" />
              <p>No S3 documents uploaded yet for {activeTenant?.name}.</p>
              <p className="text-[11px] text-slate-600">
                Click "Upload Spec" on any inventory item in the Inventory tab to store a file in S3.
              </p>
            </div>
          ) : (
            <div className="space-y-2">
              {itemsWithFiles.map((item) => (
                <div 
                  key={item.id} 
                  className="p-3.5 bg-slate-950/60 hover:bg-slate-900/80 rounded-xl border border-white/5 hover:border-cyan-500/30 flex items-center justify-between transition group"
                >
                  <div className="flex items-center space-x-3 min-w-0">
                    <div className="w-8 h-8 rounded-lg bg-cyan-500/10 border border-cyan-500/20 flex items-center justify-center text-cyan-400">
                      <FileText className="w-4 h-4" />
                    </div>
                    <div className="min-w-0">
                      <div className="text-xs font-bold text-white group-hover:text-cyan-300 truncate">
                        {item.name}
                      </div>
                      <div className="text-[10px] font-mono text-slate-400 truncate">
                        Key: <span className="text-cyan-400">{item.s3FileKey}</span>
                      </div>
                    </div>
                  </div>

                  <div className="flex items-center space-x-2 flex-shrink-0">
                    <span className="badge badge-emerald text-[10px]">S3 Encrypted</span>
                    <a
                      href={item.fileUrl || '#'}
                      target="_blank"
                      rel="noopener noreferrer"
                      className="p-1.5 rounded-lg bg-slate-800 hover:bg-slate-700 text-cyan-400 border border-white/5 transition flex items-center gap-1 text-xs"
                      title="Download with Pre-signed URL"
                    >
                      <Download className="w-3.5 h-3.5" />
                      <span className="hidden sm:inline">Download</span>
                    </a>
                  </div>
                </div>
              ))}
            </div>
          )}
        </div>

      </div>

    </div>
  );
}
