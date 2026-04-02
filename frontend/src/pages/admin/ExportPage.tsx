import { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { documentApi, configApi, exportApi } from '../../api/client';
import { Archive, Database, Download, FileDown, FileText, Filter, Loader2, X } from 'lucide-react';

const STATUS_OPTIONS = [
  'Uploaded', 'PendingProcess', 'Processing', 'PendingReview', 'ReviewInProgress',
  'Approved', 'Checked', 'Pushed', 'Failed',
];

interface DocType { id: string; displayName: string; }
interface DocItem {
  id: string;
  originalFilename: string;
  status: string;
  vendorName: string | null;
  documentTypeName: string | null;
  uploadedAt: string;
}
interface PagedDocs { items: DocItem[]; totalCount: number; page: number; pageSize: number; }

export default function ExportPage() {
  const [from, setFrom]                   = useState('');
  const [to, setTo]                       = useState('');
  const [status, setStatus]               = useState('');
  const [documentTypeId, setDocumentTypeId] = useState('');
  const [vendorName, setVendorName]       = useState('');
  const [page, setPage]                   = useState(1);
  const [csvLoading, setCsvLoading]         = useState(false);
  const [dataCsvLoading, setDataCsvLoading] = useState(false);
  const [zipLoading, setZipLoading]         = useState(false);
  const [pdfLoadingId, setPdfLoadingId]     = useState<string | null>(null);

  const { data: docTypes } = useQuery<DocType[]>({
    queryKey: ['document-types'],
    queryFn:  () => configApi.getDocumentTypes().then(r => r.data),
  });

  const filterParams: Record<string, unknown> = {
    ...(from           && { from: new Date(from).toISOString() }),
    ...(to             && { to: new Date(to + 'T23:59:59').toISOString() }),
    ...(status         && { status }),
    ...(documentTypeId && { documentTypeId }),
    ...(vendorName     && { vendorName }),
    page,
    pageSize: 20,
  };

  const { data: docs, isLoading: docsLoading } = useQuery<PagedDocs>({
    queryKey: ['export-preview', from, to, status, documentTypeId, vendorName, page],
    queryFn:  () => documentApi.list(filterParams).then(r => r.data),
    placeholderData: prev => prev,
  });

  const hasFilters = !!(from || to || status || documentTypeId || vendorName);

  const clearFilters = () => {
    setFrom(''); setTo(''); setStatus('');
    setDocumentTypeId(''); setVendorName('');
    setPage(1);
  };

  const buildFilterParams = (): Record<string, unknown> => ({
    ...(from           && { from: new Date(from).toISOString() }),
    ...(to             && { to: new Date(to + 'T23:59:59').toISOString() }),
    ...(status         && { status }),
    ...(documentTypeId && { documentTypeId }),
    ...(vendorName     && { vendorName }),
  });

  const triggerDownload = (data: BlobPart, mimeType: string, filename: string) => {
    const blob = new Blob([data], { type: mimeType });
    const url  = URL.createObjectURL(blob);
    const a    = document.createElement('a');
    a.href     = url;
    a.download = filename;
    a.click();
    URL.revokeObjectURL(url);
  };

  const exportCsv = async () => {
    setCsvLoading(true);
    try {
      const r = await exportApi.exportDocumentsCsv(buildFilterParams());
      triggerDownload(r.data as BlobPart,
        'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet',
        `documents-${new Date().toISOString().slice(0, 10)}.xlsx`);
    } finally {
      setCsvLoading(false);
    }
  };

  const exportDataCsv = async () => {
    setDataCsvLoading(true);
    try {
      const r = await exportApi.exportDocumentDataCsv(buildFilterParams());
      triggerDownload(r.data as BlobPart,
        'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet',
        `document-data-${new Date().toISOString().slice(0, 10)}.xlsx`);
    } finally {
      setDataCsvLoading(false);
    }
  };

  const exportZip = async () => {
    setZipLoading(true);
    try {
      const r = await exportApi.exportZipByVendor(buildFilterParams());
      triggerDownload(r.data as BlobPart, 'application/zip',
        `documents-by-vendor-${new Date().toISOString().slice(0, 10)}.zip`);
    } finally {
      setZipLoading(false);
    }
  };

  const downloadFile = async (docId: string) => {
    setPdfLoadingId(docId);
    try {
      const r = await exportApi.getDocumentFileUrl(docId);
      window.open(r.data.url, '_blank');
    } finally {
      setPdfLoadingId(null);
    }
  };

  const items      = docs?.items ?? [];
  const totalCount = docs?.totalCount ?? 0;
  const totalPages = Math.ceil(totalCount / 20);

  return (
    <div className="space-y-5">

      {/* ── Header ─────────────────────────────────────────────────────── */}
      <div className="flex items-center justify-between flex-wrap gap-2">
        <div>
          <h1 className="text-xl sm:text-2xl font-bold text-foreground">Export</h1>
          <p className="text-sm text-muted-foreground mt-0.5">
            Download document data as Excel or individual files
          </p>
        </div>
        <div className="flex items-center gap-2 flex-wrap">
          <button
            onClick={exportCsv}
            disabled={csvLoading}
            className="btn-secondary flex items-center gap-1.5 text-sm"
            title="Export document list as Excel"
          >
            {csvLoading ? <Loader2 className="h-4 w-4 animate-spin" /> : <Download className="h-4 w-4" />}
            List Excel
          </button>
          <button
            onClick={exportDataCsv}
            disabled={dataCsvLoading}
            className="btn-secondary flex items-center gap-1.5 text-sm"
            title="Export all extracted OCR field data as Excel"
          >
            {dataCsvLoading ? <Loader2 className="h-4 w-4 animate-spin" /> : <Database className="h-4 w-4" />}
            Data Excel
          </button>
          <button
            onClick={exportZip}
            disabled={zipLoading}
            className="btn-primary flex items-center gap-1.5 text-sm"
            title="Download all document files in a ZIP organised by vendor (max 150)"
          >
            {zipLoading ? <Loader2 className="h-4 w-4 animate-spin" /> : <Archive className="h-4 w-4" />}
            Files by Vendor (ZIP)
          </button>
        </div>
      </div>

      {/* ── Filters ────────────────────────────────────────────────────── */}
      <div className="card p-4 space-y-3">
        <div className="flex items-center gap-2 text-sm font-medium text-muted-foreground">
          <Filter className="h-4 w-4" />
          Filters
        </div>

        <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 gap-3">
          <div>
            <label className="block text-xs font-medium text-muted-foreground mb-1">From date</label>
            <input
              type="date"
              value={from}
              onChange={e => { setFrom(e.target.value); setPage(1); }}
              className="input text-sm w-full"
            />
          </div>

          <div>
            <label className="block text-xs font-medium text-muted-foreground mb-1">To date</label>
            <input
              type="date"
              value={to}
              onChange={e => { setTo(e.target.value); setPage(1); }}
              className="input text-sm w-full"
            />
          </div>

          <div>
            <label className="block text-xs font-medium text-muted-foreground mb-1">Status</label>
            <select
              value={status}
              onChange={e => { setStatus(e.target.value); setPage(1); }}
              className="input text-sm w-full"
            >
              <option value="">All statuses</option>
              {STATUS_OPTIONS.map(s => <option key={s} value={s}>{s}</option>)}
            </select>
          </div>

          <div>
            <label className="block text-xs font-medium text-muted-foreground mb-1">Document type</label>
            <select
              value={documentTypeId}
              onChange={e => { setDocumentTypeId(e.target.value); setPage(1); }}
              className="input text-sm w-full"
            >
              <option value="">All types</option>
              {docTypes?.map(dt => (
                <option key={dt.id} value={dt.id}>{dt.displayName}</option>
              ))}
            </select>
          </div>

          <div>
            <label className="block text-xs font-medium text-muted-foreground mb-1">Vendor name</label>
            <input
              type="text"
              value={vendorName}
              onChange={e => { setVendorName(e.target.value); setPage(1); }}
              placeholder="Filter by vendor…"
              className="input text-sm w-full"
            />
          </div>
        </div>

        {hasFilters && (
          <button
            onClick={clearFilters}
            className="flex items-center gap-1 text-xs text-muted-foreground hover:text-foreground"
          >
            <X className="h-3.5 w-3.5" /> Clear filters
          </button>
        )}
      </div>

      {/* ── Document preview list ───────────────────────────────────────── */}
      <div className="card overflow-x-auto">
        <div className="px-4 py-3 border-b border-border flex items-center justify-between">
          <span className="text-sm font-medium text-foreground flex items-center gap-2">
            <FileText className="h-4 w-4 text-muted-foreground" />
            {totalCount} document{totalCount !== 1 ? 's' : ''} matched
          </span>
          {totalCount > 0 && (
            <span className="text-xs text-muted-foreground hidden sm:inline">
              Use the export buttons above to download as Excel or ZIP
            </span>
          )}
        </div>

        {docsLoading ? (
          <div className="py-12 flex justify-center items-center gap-2 text-muted-foreground">
            <Loader2 className="h-4 w-4 animate-spin" />
            <span className="text-sm">Loading…</span>
          </div>
        ) : items.length === 0 ? (
          <div className="py-12 text-center text-sm text-muted-foreground">
            No documents found.
          </div>
        ) : (
          <table className="w-full text-sm">
            <thead className="bg-muted/50 border-b border-border">
              <tr>
                <th className="px-4 py-3 text-left font-medium text-muted-foreground">Filename</th>
                <th className="px-4 py-3 text-left font-medium text-muted-foreground hidden sm:table-cell">Type</th>
                <th className="px-4 py-3 text-left font-medium text-muted-foreground">Status</th>
                <th className="px-4 py-3 text-left font-medium text-muted-foreground hidden md:table-cell">Vendor</th>
                <th className="px-4 py-3 text-left font-medium text-muted-foreground hidden lg:table-cell">Uploaded</th>
                <th className="px-4 py-3 text-right font-medium text-muted-foreground">File</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-border">
              {items.map(doc => (
                <tr key={doc.id} className="hover:bg-muted/30">
                  <td className="px-4 py-3 font-medium text-foreground truncate max-w-[180px]">
                    {doc.originalFilename}
                  </td>
                  <td className="px-4 py-3 text-muted-foreground hidden sm:table-cell">
                    {doc.documentTypeName ?? '—'}
                  </td>
                  <td className="px-4 py-3">
                    <span className="text-xs px-2 py-0.5 rounded-full bg-muted text-muted-foreground">
                      {doc.status}
                    </span>
                  </td>
                  <td className="px-4 py-3 text-muted-foreground hidden md:table-cell">
                    {doc.vendorName ?? '—'}
                  </td>
                  <td className="px-4 py-3 text-muted-foreground hidden lg:table-cell">
                    {new Date(doc.uploadedAt).toLocaleDateString()}
                  </td>
                  <td className="px-4 py-3 text-right">
                    <button
                      onClick={() => downloadFile(doc.id)}
                      disabled={pdfLoadingId === doc.id}
                      className="flex items-center gap-1.5 text-xs text-primary hover:text-primary/80 ml-auto disabled:opacity-50"
                    >
                      {pdfLoadingId === doc.id
                        ? <Loader2 className="h-3.5 w-3.5 animate-spin" />
                        : <FileDown className="h-3.5 w-3.5" />}
                      Download
                    </button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}

        {/* Pagination */}
        {totalPages > 1 && (
          <div className="px-4 py-3 border-t border-border flex items-center justify-between text-sm text-muted-foreground">
            <span>Page {page} of {totalPages}</span>
            <div className="flex gap-1">
              <button
                onClick={() => setPage(p => Math.max(1, p - 1))}
                disabled={page === 1}
                className="btn-secondary text-xs px-2 py-1 disabled:opacity-50"
              >
                Prev
              </button>
              <button
                onClick={() => setPage(p => Math.min(totalPages, p + 1))}
                disabled={page === totalPages}
                className="btn-secondary text-xs px-2 py-1 disabled:opacity-50"
              >
                Next
              </button>
            </div>
          </div>
        )}
      </div>
    </div>
  );
}
