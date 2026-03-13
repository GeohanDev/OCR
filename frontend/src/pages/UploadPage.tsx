import React, { useCallback, useState, useEffect, useRef } from 'react';
import { useDropzone } from 'react-dropzone';
import { useNavigate } from 'react-router-dom';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { documentApi, configApi, ocrApi } from '../api/client';
import type { DocumentType } from '../types';
import { Upload, X, CheckCircle, AlertCircle, FileText, Loader2, ScanLine, Camera } from 'lucide-react';

type FileStatus = 'pending' | 'uploading' | 'done' | 'error';

interface FileEntry {
  file: File;
  id: string;
  status: FileStatus;
  documentId?: string;
  error?: string;
  progress: number;
}

export default function UploadPage() {
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const [files, setFiles] = useState<FileEntry[]>([]);
  const [selectedType, setSelectedType] = useState('');
  const [isUploading, setIsUploading] = useState(false);
  const [showOcrPrompt, setShowOcrPrompt] = useState(false);
  const cameraInputRef = useRef<HTMLInputElement>(null);

  const { data: docTypes } = useQuery<DocumentType[]>({
    queryKey: ['document-types'],
    queryFn: () => configApi.getDocumentTypes().then(r => r.data),
  });

  const [dropErrors, setDropErrors] = useState<string[]>([]);
  // Files that conflict with existing queue entries (same name) — pending user decision
  const [duplicates, setDuplicates] = useState<File[]>([]);

  const onDrop = useCallback((accepted: File[]) => {
    if (!selectedType) return;
    setDropErrors([]);
    setFiles(prev => {
      const existingNames = new Set(prev.map(f => f.file.name));
      const conflicts: File[] = [];
      const fresh: File[] = [];
      for (const file of accepted) {
        if (existingNames.has(file.name)) {
          conflicts.push(file);
        } else {
          fresh.push(file);
        }
      }
      if (conflicts.length > 0) setDuplicates(conflicts);
      const newEntries: FileEntry[] = fresh.map(file => ({
        file,
        id: `${file.name}-${Date.now()}-${Math.random()}`,
        status: 'pending',
        progress: 0,
      }));
      return [...prev, ...newEntries];
    });
  }, [selectedType]);

  // Handle photo capture from the device camera
  const handleCameraCapture = useCallback((e: React.ChangeEvent<HTMLInputElement>) => {
    const captured = Array.from(e.target.files ?? []);
    if (captured.length === 0) return;
    // Reset input so the same photo can be re-captured if needed
    e.target.value = '';
    onDrop(captured);
  }, [onDrop]);

  const handleDuplicateReplace = () => {
    setFiles(prev => {
      const dupNames = new Set(duplicates.map(f => f.name));
      const kept = prev.filter(e => !dupNames.has(e.file.name));
      const replacements: FileEntry[] = duplicates.map(file => ({
        file,
        id: `${file.name}-${Date.now()}-${Math.random()}`,
        status: 'pending',
        progress: 0,
      }));
      return [...kept, ...replacements];
    });
    setDuplicates([]);
  };

  const handleDuplicateSkip = () => setDuplicates([]);

  // Some browsers report PDFs as application/octet-stream; validate by extension too.
  const allowedExtensions = ['pdf', 'png', 'jpg', 'jpeg', 'tif', 'tiff'];
  const fileValidator = (file: File) => {
    const ext = file.name.split('.').pop()?.toLowerCase() ?? '';
    if (!allowedExtensions.includes(ext)) {
      return { code: 'wrong-file-type', message: `"${file.name}" is not allowed. Only PDF, PNG, JPG, and TIFF files are accepted.` };
    }
    return null;
  };

  const { getRootProps, getInputProps, isDragActive } = useDropzone({
    onDrop,
    accept: {
      'application/pdf': ['.pdf'],
      'image/png': ['.png'],
      'image/jpeg': ['.jpg', '.jpeg'],
      'image/tiff': ['.tif', '.tiff'],
      // Fallback: some browsers/OSes report PDFs as octet-stream
      'application/octet-stream': ['.pdf', '.png', '.jpg', '.jpeg', '.tif', '.tiff'],
    },
    validator: fileValidator,
    maxSize: 50 * 1024 * 1024, // 50 MB
    onDropRejected: (rejections) => {
      setDropErrors(rejections.map(r =>
        r.errors[0]?.message ?? `"${r.file.name}" was rejected.`
      ));
    },
  });

  const removeFile = (id: string) => {
    setFiles(prev => prev.filter(f => f.id !== id));
  };

  const handleUpload = async () => {
    const pending = files.filter(f => f.status === 'pending');
    if (pending.length === 0) return;
    setIsUploading(true);

    for (const entry of pending) {
      setFiles(prev => prev.map(f => f.id === entry.id ? { ...f, status: 'uploading', progress: 0 } : f));
      try {
        const formData = new FormData();
        formData.append('files', entry.file);
        if (selectedType) formData.append('documentTypeId', selectedType);

        const res = await documentApi.upload(formData);
        // Backend returns [{ success: boolean, document?: DocumentDto, error?: string }]
        const item = res.data[0];
        if (item?.success === false) {
          setFiles(prev => prev.map(f =>
            f.id === entry.id ? { ...f, status: 'error', error: item.error ?? 'Upload failed' } : f
          ));
        } else {
          const docId = item?.document?.id;
          setFiles(prev => prev.map(f =>
            f.id === entry.id ? { ...f, status: 'done', progress: 100, documentId: docId } : f
          ));
        }
      } catch (err: unknown) {
        const errData = (err as { response?: { data?: unknown } })?.response?.data;
        const message =
          (typeof errData === 'string' ? errData : null) ??
          (errData as { detail?: string })?.detail ??
          (errData as { error?: string })?.error ??
          'Upload failed';
        setFiles(prev => prev.map(f =>
          f.id === entry.id ? { ...f, status: 'error', error: message } : f
        ));
      }
    }
    setIsUploading(false);
    queryClient.invalidateQueries({ queryKey: ['documents'] });
  };

  const allDone = files.length > 0 && files.every(f => f.status === 'done' || f.status === 'error')
    && files.some(f => f.status === 'done');
  const hasPending = files.some(f => f.status === 'pending');
  const selectedTypeLabel = selectedType
    ? (docTypes?.find(dt => dt.id === selectedType)?.displayName ?? 'Unknown type')
    : 'Auto-detect';

  // After all uploads finish, show the OCR & validation prompt instead of auto-redirecting.
  useEffect(() => {
    if (allDone) setShowOcrPrompt(true);
  }, [allDone]);

  const handleStartOcrAndValidation = () => {
    const done = files.filter(f => f.status === 'done' && f.documentId);
    // Fire OCR requests without awaiting — the API returns 202 immediately and
    // processes in the background. Navigate away right away so the user isn't blocked.
    done.forEach(d => ocrApi.process(d.documentId!).catch(() => {}));
    if (done.length === 1 && done[0].documentId) {
      navigate(`/documents/${done[0].documentId}`);
    } else {
      navigate('/documents');
    }
  };

  const handleSkipOcr = () => {
    const done = files.filter(f => f.status === 'done');
    if (done.length === 1 && done[0].documentId) {
      navigate(`/documents/${done[0].documentId}`);
    } else {
      navigate('/documents');
    }
  };

  return (
    <div className="space-y-6 max-w-2xl mx-auto">
      <h1 className="text-xl sm:text-2xl font-bold text-gray-900">Upload Documents</h1>

      {/* Document type selector — required before upload */}
      <div className={`card p-4 ${!selectedType ? 'ring-2 ring-amber-400 ring-offset-1' : 'ring-2 ring-green-400 ring-offset-1'}`}>
        <label className="block text-sm font-medium text-gray-700 mb-1">
          Document Type <span className="text-red-500">*</span>
        </label>
        <p className="text-xs text-gray-500 mb-2">Select the document type before uploading.</p>
        <select
          className="input w-full"
          value={selectedType}
          onChange={e => setSelectedType(e.target.value)}
          disabled={isUploading}
        >
          <option value="">— Select document type —</option>
          {docTypes?.map(dt => (
            <option key={dt.id} value={dt.id}>{dt.displayName}</option>
          ))}
        </select>
        {!selectedType && (
          <p className="text-xs text-amber-600 mt-1.5 flex items-center gap-1">
            <AlertCircle className="h-3 w-3" /> Please select a document type to continue.
          </p>
        )}
      </div>

      {/* Duplicate filename modal */}
      {duplicates.length > 0 && (
        <div className="fixed inset-0 z-50 flex items-center justify-center p-4 bg-black/50">
          <div className="card p-6 w-full max-w-md space-y-4">
            <h3 className="font-semibold text-gray-900 flex items-center gap-2">
              <AlertCircle className="h-5 w-5 text-yellow-500" />
              Duplicate {duplicates.length === 1 ? 'Filename' : 'Filenames'} Detected
            </h3>
            <p className="text-sm text-gray-600">
              The following {duplicates.length === 1 ? 'file is' : 'files are'} already in the upload queue:
            </p>
            <ul className="text-sm font-medium text-gray-800 space-y-1 max-h-32 overflow-y-auto">
              {duplicates.map(f => (
                <li key={f.name} className="truncate">• {f.name}</li>
              ))}
            </ul>
            <p className="text-sm text-gray-600">Do you want to replace the existing {duplicates.length === 1 ? 'entry' : 'entries'} or skip?</p>
            <div className="flex justify-end gap-3">
              <button className="btn-secondary" onClick={handleDuplicateSkip}>Skip</button>
              <button className="btn-primary bg-yellow-600 hover:bg-yellow-700" onClick={handleDuplicateReplace}>
                Replace
              </button>
            </div>
          </div>
        </div>
      )}

      {/* Post-upload OCR & validation prompt */}
      {showOcrPrompt && (
        <div className="fixed inset-0 z-50 flex items-center justify-center p-4 bg-black/50">
          <div className="card p-6 w-full max-w-md space-y-4">
            <h3 className="font-semibold text-gray-900 flex items-center gap-2">
              <CheckCircle className="h-5 w-5 text-green-500" />
              Upload Complete
            </h3>

            <div className="rounded-lg bg-gray-50 border border-gray-200 p-3 space-y-1 text-sm">
              <p className="text-gray-600">
                <span className="font-medium text-gray-800">
                  {files.filter(f => f.status === 'done').length}
                </span>{' '}
                file(s) uploaded successfully.
              </p>
              <p className="text-gray-600">
                Document type:{' '}
                <span className="font-medium text-gray-900">{selectedTypeLabel}</span>
              </p>
            </div>

            <div className="flex items-start gap-3 rounded-lg bg-blue-50 border border-blue-100 p-3">
              <ScanLine className="h-5 w-5 text-blue-500 flex-shrink-0 mt-0.5" />
              <p className="text-sm text-blue-800">
                Would you like to start OCR extraction and field validation now?
              </p>
            </div>

            <div className="flex justify-end gap-3 pt-1">
              <button className="btn-secondary" onClick={handleSkipOcr}>
                Skip for Now
              </button>
              <button
                className="btn-primary flex items-center gap-2"
                onClick={handleStartOcrAndValidation}
              >
                <ScanLine className="h-4 w-4" /> Start OCR & Validation
              </button>
            </div>
          </div>
        </div>
      )}

      {/* Drop rejection errors */}
      {dropErrors.length > 0 && (
        <div className="rounded-lg border border-red-200 bg-red-50 p-3 space-y-1">
          {dropErrors.map((msg, i) => (
            <p key={i} className="text-sm text-red-700 flex items-start gap-2">
              <AlertCircle className="h-4 w-4 flex-shrink-0 mt-0.5" />
              {msg}
            </p>
          ))}
        </div>
      )}

      {/* Hidden camera input — capture="environment" opens the rear camera on mobile */}
      <input
        ref={cameraInputRef}
        type="file"
        accept="image/*"
        capture="environment"
        className="hidden"
        onChange={handleCameraCapture}
        disabled={!selectedType || isUploading}
      />

      {/* Drop zone */}
      <div
        {...getRootProps()}
        className={`border-2 border-dashed rounded-xl p-6 sm:p-10 text-center transition-colors ${
          !selectedType
            ? 'border-gray-200 bg-gray-50 cursor-not-allowed opacity-60'
            : isDragActive ? 'border-blue-400 bg-blue-50 cursor-pointer'
            : 'border-gray-300 hover:border-gray-400 bg-white cursor-pointer'
        }`}
        {...(!selectedType ? { onClick: (e: React.MouseEvent) => e.stopPropagation() } : {})}
      >
        <input {...getInputProps()} />
        <Upload className="h-8 w-8 sm:h-10 sm:w-10 text-gray-400 mx-auto mb-3" />
        {isDragActive ? (
          <p className="text-blue-600 font-medium">Drop files here...</p>
        ) : (
          <>
            <p className="text-gray-700 font-medium text-sm sm:text-base">
              <span className="hidden sm:inline">Drag & drop files here, or </span>
              <span className="text-primary font-semibold">Tap to browse files</span>
            </p>
            <p className="text-xs sm:text-sm text-gray-500 mt-1">PDF, PNG, JPG, TIFF — up to 50 MB each</p>
          </>
        )}
      </div>

      {/* Camera button — only shown on mobile (sm:hidden), only when a type is selected */}
      {selectedType && (
        <button
          type="button"
          onClick={() => cameraInputRef.current?.click()}
          disabled={isUploading}
          className="sm:hidden w-full flex items-center justify-center gap-3 py-3.5 rounded-xl border-2 border-dashed border-primary/40 bg-primary/5 text-primary font-medium text-sm hover:bg-primary/10 active:bg-primary/15 transition-colors disabled:opacity-50"
        >
          <Camera className="h-5 w-5" />
          Take Photo with Camera
        </button>
      )}

      {/* File queue */}
      {files.length > 0 && (
        <div className="card divide-y divide-gray-100">
          {files.map(entry => (
            <div key={entry.id} className="flex items-center gap-3 p-4">
              <FileText className="h-5 w-5 text-gray-400 flex-shrink-0" />
              <div className="flex-1 min-w-0">
                <p className="text-sm font-medium text-gray-900 truncate">{entry.file.name}</p>
                <p className="text-xs text-gray-500">{(entry.file.size / 1024).toFixed(0)} KB</p>
                {entry.status === 'uploading' && (
                  <div className="mt-1 h-1 bg-gray-200 rounded-full overflow-hidden">
                    <div className="h-full bg-blue-500 rounded-full animate-pulse w-3/4" />
                  </div>
                )}
                {entry.status === 'error' && (
                  <p className="text-xs text-red-600 mt-1">{entry.error}</p>
                )}
              </div>
              <div className="flex-shrink-0 flex items-center gap-2">
                {entry.status === 'pending' && (
                  <span className="text-xs text-gray-400 badge bg-gray-100 text-gray-600">Ready</span>
                )}
                {entry.status === 'uploading' && (
                  <Loader2 className="h-4 w-4 text-blue-500 animate-spin" />
                )}
                {entry.status === 'done' && (
                  <CheckCircle className="h-5 w-5 text-green-500" />
                )}
                {entry.status === 'error' && (
                  <AlertCircle className="h-5 w-5 text-red-500" />
                )}
                {(entry.status === 'pending' || entry.status === 'error') && (
                  <button onClick={() => removeFile(entry.id)} className="text-gray-400 hover:text-gray-600">
                    <X className="h-4 w-4" />
                  </button>
                )}
                {entry.status === 'done' && entry.documentId && (
                  <button
                    onClick={() => navigate(`/documents/${entry.documentId}`)}
                    className="text-xs text-blue-600 hover:underline"
                  >
                    View
                  </button>
                )}
              </div>
            </div>
          ))}
        </div>
      )}

      {/* Actions */}
      <div className="flex items-center justify-between">
        <button
          className="btn-secondary"
          onClick={() => navigate('/documents')}
        >
          Cancel
        </button>
        <div className="flex gap-3">
          {allDone && (
            <button className="btn-secondary" onClick={() => navigate('/documents')}>
              Go to Documents
            </button>
          )}
          {hasPending && (
            <button
              className="btn-primary flex items-center gap-2"
              onClick={handleUpload}
              disabled={isUploading || !selectedType}
              title={!selectedType ? 'Select a document type first' : undefined}
            >
              {isUploading && <Loader2 className="h-4 w-4 animate-spin" />}
              Upload {files.filter(f => f.status === 'pending').length} file(s)
            </button>
          )}
        </div>
      </div>
    </div>
  );
}
