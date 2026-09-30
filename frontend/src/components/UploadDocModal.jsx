import React, { useState } from 'react';
import { X, UploadCloud, FileCheck, AlertCircle, HardDrive } from 'lucide-react';

export default function UploadDocModal({ item, isOpen, onClose, onUpload, activeTenant }) {
  const [selectedFile, setSelectedFile] = useState(null);
  const [uploading, setUploading] = useState(false);
  const [dragActive, setDragActive] = useState(false);

  if (!isOpen || !item) return null;

  const handleFileChange = (e) => {
    if (e.target.files && e.target.files[0]) {
      setSelectedFile(e.target.files[0]);
    }
  };

  const handleDrop = (e) => {
    e.preventDefault();
    setDragActive(false);
    if (e.dataTransfer.files && e.dataTransfer.files[0]) {
      setSelectedFile(e.dataTransfer.files[0]);
    }
  };

  const handleSubmit = async (e) => {
    e.preventDefault();
    if (!selectedFile) return;
    setUploading(true);
    try {
      await onUpload(item.id, selectedFile);
      onClose();
    } catch (err) {
      console.error(err);
    } finally {
      setUploading(false);
    }
  };

  return (
    <div className="modal-overlay">
      <div className="modal-content p-6 max-w-lg w-full">
        <div className="flex items-center justify-between pb-4 border-b border-white/10">
          <div className="flex items-center space-x-2">
            <UploadCloud className="w-5 h-5 text-cyan-400" />
            <h3 className="text-base font-bold text-white">Upload S3 Spec Sheet / Asset</h3>
          </div>
          <button onClick={onClose} className="p-1 rounded-lg hover:bg-slate-800 text-slate-400 hover:text-white">
            <X className="w-5 h-5" />
          </button>
        </div>

        <form onSubmit={handleSubmit} className="mt-4 space-y-4">
          <div className="p-3 bg-slate-950/60 rounded-xl border border-white/5">
            <div className="text-xs text-slate-400">Attaching Document for SKU:</div>
            <div className="text-sm font-bold text-white">{item.name}</div>
            <div className="text-xs font-mono text-cyan-400 mt-1">
              S3 Partition: s3://acentra-inventory/{activeTenant?.code}/docs/{item.sku}/
            </div>
          </div>

          <div
            onDragOver={(e) => { e.preventDefault(); setDragActive(true); }}
            onDragLeave={() => setDragActive(false)}
            onDrop={handleDrop}
            className={`border-2 border-dashed rounded-2xl p-8 text-center transition-all cursor-pointer ${
              dragActive 
                ? 'border-cyan-400 bg-cyan-950/20' 
                : 'border-white/10 hover:border-cyan-500/40 bg-slate-950/40'
            }`}
            onClick={() => document.getElementById('file-upload-input').click()}
          >
            <input
              id="file-upload-input"
              type="file"
              className="hidden"
              onChange={handleFileChange}
              accept=".pdf,.png,.jpg,.jpeg,.docx,.xlsx"
            />

            {selectedFile ? (
              <div className="space-y-2">
                <FileCheck className="w-10 h-10 mx-auto text-emerald-400" />
                <div className="text-sm font-semibold text-white">{selectedFile.name}</div>
                <div className="text-xs text-slate-400">
                  {(selectedFile.size / 1024).toFixed(1)} KB | Ready to upload to S3
                </div>
              </div>
            ) : (
              <div className="space-y-2">
                <UploadCloud className="w-10 h-10 mx-auto text-slate-400" />
                <div className="text-sm font-semibold text-slate-200">
                  Drag and drop files here, or <span className="text-cyan-400">browse</span>
                </div>
                <p className="text-xs text-slate-500">
                  Supports PDF spec sheets, MSDS certificates, lab reports, high-res images
                </p>
              </div>
            )}
          </div>

          <div className="flex items-center space-x-2 text-xs text-slate-400">
            <HardDrive className="w-4 h-4 text-cyan-400 flex-shrink-0" />
            <span>Files are encrypted & strictly partitioned by Tenant ID on AWS S3.</span>
          </div>

          <div className="flex items-center justify-end space-x-3 pt-3 border-t border-white/10">
            <button type="button" onClick={onClose} className="btn-secondary text-xs">
              Cancel
            </button>
            <button 
              type="submit" 
              disabled={!selectedFile || uploading} 
              className="btn-primary text-xs"
            >
              {uploading ? 'Encrypting & Uploading to S3...' : 'Upload to S3 Bucket'}
            </button>
          </div>
        </form>
      </div>
    </div>
  );
}
