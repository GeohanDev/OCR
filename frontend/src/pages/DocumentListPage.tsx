import { useState, useEffect, useMemo } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { Link } from 'react-router-dom';
import { documentApi, vendorApi } from '../api/client';
import StatusBadge from '../components/ui/StatusBadge';
import { useAuth } from '../contexts/AuthContext';
import type { PagedResult, Document, Vendor } from '../types';
import {
  Upload, Search, ChevronLeft, ChevronRight,
  Trash2, Loader2, Download, Eye, Building2, X,
} from 'lucide-react';

const STATUS_OPTIONS = ['', 'Uploaded', 'Processing', 'PendingReview', 'Checked'];

export default function DocumentListPage() {
  const [page, setPage] = useState(1);
  const [status, setStatus] = useState('');
  const [search, setSearch] = useState('');
  const [searchInput, setSearchInput] = useState('');
  const [vendorId, setVendorId] = useState('');
  const [groupByVendor, setGroupByVendor] = useState(false);
  const [confirmDeleteId, setConfirmDeleteId] = useState<string | null>(null);
  const [downloadingId, setDownloadingId] = useState<string | null>(null);
  const [previewingId, setPreviewingId] = useState<string | null>(null);

  const handlePreview = async (docId: string) => {
    setPreviewingId(docId);
    try {
      const res = await documentApi.getSignedUrl(docId);
      window.open(res.data.url, '_blank');
    } finally {
      setPreviewingId(null);
    }
  };

  const handleDownload = async (docId: string, filename: string) => {
    setDownloadingId(docId);
    try {
      const res = await documentApi.getSignedUrl(docId);
      const link = document.createElement('a');
      link.href = res.data.url;
      link.setAttribute('download', filename);
      link.setAttribute('target', '_blank');
      document.body.appendChild(link);
      link.click();
      document.body.removeChild(link);
    } finally {
      setDownloadingId(null);
    }
  };

  useEffect(() => {
    const t = setTimeout(() => { setSearch(searchInput); setPage(1); }, 400);
    return () => clearTimeout(t);
  }, [searchInput]);

  const { isAdmin } = useAuth();
  const queryClient = useQueryClient();

  const { data, isLoading } = useQuery<PagedResult<Document>>({
    queryKey: ['documents', page, status, search, vendorId],
    queryFn: () => documentApi.list({
      page,
      pageSize: groupByVendor ? 100 : 20,
      status: status || undefined,
      search: search || undefined,
      vendorId: vendorId || undefined,
    }).then(r => r.data),
    refetchInterval: 30000,
  });

  // Vendor list for filter dropdown (Manager+ only — gracefully empty if forbidden)
  const { data: vendorData } = useQuery<PagedResult<Vendor>>({
    queryKey: ['vendors-filter'],
    queryFn: () => vendorApi.list({ pageSize: 500 }).then(r => r.data).catch(() => ({
      items: [], totalCount: 0, page: 1, pageSize: 500,
      totalPages: 0, hasNextPage: false, hasPreviousPage: false,
    })),
    staleTime: 60_000,
  });

  const vendors = vendorData?.items ?? [];

  const deleteDoc = useMutation({
    mutationFn: (id: string) => documentApi.delete(id),
    onSuccess: () => {
      setConfirmDeleteId(null);
      queryClient.invalidateQueries({ queryKey: ['documents'] });
    },
  });

  // Group documents by vendor name (case-insensitive) when groupByVendor is on
  const groupedItems = useMemo(() => {
    if (!groupByVendor || !data?.items) return null;
    // Use lowercase key for grouping but preserve the first-seen display label per group
    const groups: Record<string, { label: string; docs: Document[] }> = {};
    for (const doc of data.items) {
      const raw = doc.vendorName?.trim() || '';
      const key = raw ? raw.toLowerCase() : '__no_vendor__';
      if (!groups[key]) groups[key] = { label: raw || 'No Vendor Assigned', docs: [] };
      groups[key].docs.push(doc);
    }
    return Object.entries(groups)
      .sort(([a, { label: la }], [b, { label: lb }]) => {
        if (a === '__no_vendor__') return 1;
        if (b === '__no_vendor__') return -1;
        return la.localeCompare(lb);
      })
      .map(([, { label, docs }]) => [label, docs] as [string, Document[]]);
  }, [groupByVendor, data?.items]);

  const colSpan = isAdmin ? 7 : 6;

  const DocRow = ({ doc }: { doc: Document }) => (
    <tr key={doc.id} className="hover:bg-muted/30">
      <td className="px-4 py-3">
        <Link to={`/documents/${doc.id}`} className="text-primary hover:underline font-medium">
          {doc.originalFilename}
        </Link>
      </td>
      <td className="px-4 py-3 text-muted-foreground">{doc.documentTypeName ?? '—'}</td>
      <td className="px-4 py-3"><StatusBadge status={doc.status} /></td>
      <td className="px-4 py-3 text-muted-foreground">{doc.uploadedByUsername}</td>
      <td className="px-4 py-3 text-muted-foreground">{new Date(doc.uploadedAt).toLocaleDateString()}</td>
      <td className="px-4 py-3 text-right">
        <div className="flex items-center justify-end gap-2">
          <button
            onClick={() => handlePreview(doc.id)}
            disabled={previewingId === doc.id}
            className="text-gray-400 hover:text-blue-600 transition-colors disabled:opacity-50"
            title="Preview"
          >
            {previewingId === doc.id ? <Loader2 className="h-4 w-4 animate-spin" /> : <Eye className="h-4 w-4" />}
          </button>
          <button
            onClick={() => handleDownload(doc.id, doc.originalFilename)}
            disabled={downloadingId === doc.id}
            className="text-gray-400 hover:text-blue-600 transition-colors disabled:opacity-50"
            title="Download"
          >
            {downloadingId === doc.id ? <Loader2 className="h-4 w-4 animate-spin" /> : <Download className="h-4 w-4" />}
          </button>
        </div>
      </td>
      {isAdmin && (
        <td className="px-4 py-3 text-right">
          <button
            onClick={() => setConfirmDeleteId(doc.id)}
            className="text-gray-400 hover:text-red-600 transition-colors"
            title="Delete"
          >
            <Trash2 className="h-4 w-4" />
          </button>
        </td>
      )}
    </tr>
  );

  const TableHead = () => (
    <thead className="bg-muted/50 border-b border-border">
      <tr>
        <th className="px-4 py-3 text-left font-medium text-muted-foreground">Filename</th>
        <th className="px-4 py-3 text-left font-medium text-muted-foreground">Type</th>
        <th className="px-4 py-3 text-left font-medium text-muted-foreground">Status</th>
        <th className="px-4 py-3 text-left font-medium text-muted-foreground">Uploaded by</th>
        <th className="px-4 py-3 text-left font-medium text-muted-foreground">Date</th>
        <th className="px-4 py-3" />
        {isAdmin && <th className="px-4 py-3" />}
      </tr>
    </thead>
  );

  const activeFiltersCount = [status, search, vendorId].filter(Boolean).length;

  return (
    <div className="space-y-4">
      {/* Header */}
      <div className="flex items-center justify-between">
        <h1 className="text-2xl font-bold text-foreground">Documents</h1>
        <Link to="/documents/upload" className="btn-primary flex items-center gap-2">
          <Upload className="h-4 w-4" />
          Upload
        </Link>
      </div>

      {/* Filters */}
      <div className="card p-4 space-y-3">
        <div className="flex flex-wrap gap-3">
          {/* Search */}
          <div className="relative flex-1 min-w-[200px]">
            <Search className="absolute left-3 top-1/2 -translate-y-1/2 h-4 w-4 text-muted-foreground" />
            <input
              className="input pl-9 w-full"
              placeholder="Search by filename or vendor..."
              value={searchInput}
              onChange={e => setSearchInput(e.target.value)}
            />
          </div>

          {/* Status */}
          <select
            className="input w-auto"
            value={status}
            onChange={e => { setStatus(e.target.value); setPage(1); }}
          >
            {STATUS_OPTIONS.map(s => <option key={s} value={s}>{s || 'All statuses'}</option>)}
          </select>

          {/* Vendor filter */}
          {vendors.length > 0 && (
            <div className="relative">
              <Building2 className="absolute left-3 top-1/2 -translate-y-1/2 h-4 w-4 text-muted-foreground" />
              <select
                className="input pl-9 w-auto"
                value={vendorId}
                onChange={e => { setVendorId(e.target.value); setPage(1); }}
              >
                <option value="">All vendors</option>
                {vendors.map(v => (
                  <option key={v.id} value={v.id}>{v.vendorName}</option>
                ))}
              </select>
            </div>
          )}
        </div>

        {/* Second row: group toggle + active filter chips */}
        <div className="flex items-center gap-3 flex-wrap">
          <label className="flex items-center gap-2 text-sm text-muted-foreground cursor-pointer select-none">
            <input
              type="checkbox"
              className="rounded"
              checked={groupByVendor}
              onChange={e => setGroupByVendor(e.target.checked)}
            />
            Group by vendor
          </label>

          {activeFiltersCount > 0 && (
            <div className="flex items-center gap-2 flex-wrap">
              {status && (
                <span className="inline-flex items-center gap-1 px-2 py-0.5 rounded-full text-xs bg-primary/10 text-primary">
                  {status}
                  <button onClick={() => setStatus('')}><X className="h-3 w-3" /></button>
                </span>
              )}
              {search && (
                <span className="inline-flex items-center gap-1 px-2 py-0.5 rounded-full text-xs bg-primary/10 text-primary">
                  "{search}"
                  <button onClick={() => { setSearch(''); setSearchInput(''); }}><X className="h-3 w-3" /></button>
                </span>
              )}
              {vendorId && (
                <span className="inline-flex items-center gap-1 px-2 py-0.5 rounded-full text-xs bg-primary/10 text-primary">
                  {vendors.find(v => v.id === vendorId)?.vendorName ?? 'Vendor'}
                  <button onClick={() => setVendorId('')}><X className="h-3 w-3" /></button>
                </span>
              )}
            </div>
          )}
        </div>
      </div>

      {/* Table (desktop) — grouped or flat */}
      <div className="card hidden md:block overflow-hidden">
        {groupByVendor && groupedItems ? (
          /* Grouped view */
          groupedItems.map(([vendorGroup, docs]) => (
            <div key={vendorGroup}>
              <div className="flex items-center gap-2 px-4 py-2 bg-muted/60 border-b border-border">
                <Building2 className="h-4 w-4 text-muted-foreground" />
                <span className="text-sm font-semibold text-foreground">{vendorGroup}</span>
                <span className="text-xs text-muted-foreground ml-1">({docs.length})</span>
              </div>
              <table className="w-full text-sm">
                <TableHead />
                <tbody className="divide-y divide-border">
                  {docs.map(doc => <DocRow key={doc.id} doc={doc} />)}
                </tbody>
              </table>
            </div>
          ))
        ) : (
          /* Flat view */
          <table className="w-full text-sm">
            <TableHead />
            <tbody className="divide-y divide-border">
              {isLoading && (
                <tr><td colSpan={colSpan} className="px-4 py-8 text-center text-muted-foreground">Loading...</td></tr>
              )}
              {data?.items.map(doc => <DocRow key={doc.id} doc={doc} />)}
              {!isLoading && data?.items.length === 0 && (
                <tr><td colSpan={colSpan} className="px-4 py-8 text-center text-muted-foreground">No documents found.</td></tr>
              )}
            </tbody>
          </table>
        )}
      </div>

      {/* Card list (mobile) */}
      <div className="md:hidden space-y-3">
        {data?.items.map(doc => (
          <Link key={doc.id} to={`/documents/${doc.id}`} className="card p-4 block">
            <div className="flex items-start justify-between mb-2">
              <p className="text-sm font-medium text-foreground truncate pr-2">{doc.originalFilename}</p>
              <StatusBadge status={doc.status} />
            </div>
            {doc.vendorName && (
              <p className="text-xs text-blue-600 flex items-center gap-1 mb-1">
                <Building2 className="h-3 w-3" />{doc.vendorName}
              </p>
            )}
            <p className="text-xs text-muted-foreground">{doc.uploadedByUsername} · {new Date(doc.uploadedAt).toLocaleDateString()}</p>
          </Link>
        ))}
      </div>

      {/* Delete modal */}
      {confirmDeleteId && (
        <div className="fixed inset-0 z-50 flex items-center justify-center p-4 bg-black/50">
          <div className="card p-6 w-full max-w-sm space-y-4">
            <h3 className="font-semibold text-foreground">Delete Document</h3>
            <p className="text-sm text-muted-foreground">
              Are you sure you want to delete "
              <span className="font-medium">{data?.items.find(d => d.id === confirmDeleteId)?.originalFilename}</span>
              "? This action cannot be undone.
            </p>
            <div className="flex justify-end gap-3">
              <button className="btn-secondary" onClick={() => setConfirmDeleteId(null)}>Cancel</button>
              <button
                className="btn-primary bg-red-600 hover:bg-red-700 flex items-center gap-2"
                onClick={() => deleteDoc.mutate(confirmDeleteId)}
                disabled={deleteDoc.isPending}
              >
                {deleteDoc.isPending && <Loader2 className="h-4 w-4 animate-spin" />}
                Delete
              </button>
            </div>
          </div>
        </div>
      )}

      {/* Pagination (only in flat view) */}
      {!groupByVendor && data && data.totalPages > 1 && (
        <div className="flex items-center justify-between">
          <p className="text-sm text-muted-foreground">
            Showing {(page - 1) * 20 + 1}–{Math.min(page * 20, data.totalCount)} of {data.totalCount}
          </p>
          <div className="flex gap-2">
            <button className="btn-secondary p-2" disabled={!data.hasPreviousPage} onClick={() => setPage(p => p - 1)}>
              <ChevronLeft className="h-4 w-4" />
            </button>
            <button className="btn-secondary p-2" disabled={!data.hasNextPage} onClick={() => setPage(p => p + 1)}>
              <ChevronRight className="h-4 w-4" />
            </button>
          </div>
        </div>
      )}
    </div>
  );
}
