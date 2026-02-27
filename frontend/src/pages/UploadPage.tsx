import { useCallback, useState } from 'react';
import { useDropzone } from 'react-dropzone';
import { useNavigate } from 'react-router-dom';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { documentApi, configApi } from '../api/client';
import type { DocumentType } from '../types';
import { Upload, X, CheckCircle, AlertCircle, FileText, Loader2 } from 'lucide-react';

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

  const { data: docTypes } = useQuery<DocumentType[]>({
    queryKey: ['document-types'],
    queryFn: () => configApi.getDocumentTypes().then(r => r.data),
  });

  const [dropErrors, setDropErrors] = useState<string[]>([]);

  const onDrop = useCallback((accepted: File[]) => {
    setDropErrors([]);
    const newEntries: FileEntry[] = accepted.map(file => ({
      file,
      id: `${file.name}-${Date.now()}-${Math.random()}`,
      status: 'pending',
      progress: 0,
    }));
    setFiles(prev => [...prev, ...newEntries]);
  }, []);

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

  const allDone = files.length > 0 && files.every(f => f.status === 'done');
  const hasPending = files.some(f => f.status === 'pending');

  return (
    <div className="space-y-6 max-w-2xl mx-auto">
      <h1 className="text-2xl font-bold text-gray-900">Upload Documents</h1>

      {/* Document type selector */}
      <div className="card p-4">
        <label className="block text-sm font-medium text-gray-700 mb-2">Document Type (optional)</label>
        <select
          className="input w-full"
          value={selectedType}
          onChange={e => setSelectedType(e.target.value)}
          disabled={isUploading}
        >
          <option value="">Auto-detect</option>
          {docTypes?.map(dt => (
            <option key={dt.id} value={dt.id}>{dt.displayName}</option>
          ))}
        </select>
      </div>

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

      {/* Drop zone */}
      <div
        {...getRootProps()}
        className={`border-2 border-dashed rounded-xl p-10 text-center cursor-pointer transition-colors ${
          isDragActive ? 'border-blue-400 bg-blue-50' : 'border-gray-300 hover:border-gray-400 bg-white'
        }`}
      >
        <input {...getInputProps()} />
        <Upload className="h-10 w-10 text-gray-400 mx-auto mb-3" />
        {isDragActive ? (
          <p className="text-blue-600 font-medium">Drop files here...</p>
        ) : (
          <>
            <p className="text-gray-700 font-medium">Drag & drop files here, or click to browse</p>
            <p className="text-sm text-gray-500 mt-1">PDF, PNG, JPG, TIFF — up to 50 MB each</p>
          </>
        )}
      </div>

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
              disabled={isUploading}
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
