import { useState, useEffect, useMemo, useRef } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { Link } from 'react-router-dom';
import { documentApi, configApi, branchApi } from '../api/client';
import StatusBadge from '../components/ui/StatusBadge';
import { useAuth } from '../contexts/AuthContext';
import type { PagedResult, Document, DocumentType } from '../types';

interface Branch { id: string; branchCode: string; branchName: string; }
import {
  Upload, Search, ChevronLeft, ChevronRight,
  Trash2, Loader2, Download, Eye, Building2, X, MoreVertical, FileType, ChevronDown, AlertTriangle,
} from 'lucide-react';

const STATUS_OPTIONS = ['', 'Uploaded', 'PendingProcess', 'Processing', 'PendingReview', 'Checked'];

// Custom dropdown that always opens downward (avoids native <select> opening upward near bottom)
function CustomSelect({ value, onChange, options, placeholder, icon: Icon, disabled }: {
  value: string;
  onChange: (v: string) => void;
  options: { value: string; label: string }[];
  placeholder: string;
  icon?: React.ElementType;
  disabled?: boolean;
}) {
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);

  useEffect(() => {
    const handler = (e: MouseEvent) => {
      if (ref.current && !ref.current.contains(e.target as Node)) setOpen(false);
    };
    document.addEventListener('mousedown', handler);
    return () => document.removeEventListener('mousedown', handler);
  }, []);

  const selected = options.find(o => o.value === value);

  return (
    <div className="relative" ref={ref}>
      {Icon && (
        <Icon className="absolute left-2.5 top-1/2 -translate-y-1/2 h-3.5 w-3.5 text-muted-foreground pointer-events-none z-10" />
      )}
      <button
        type="button"
        disabled={disabled}
        onClick={() => setOpen(o => !o)}
        className={`input text-sm w-full text-left flex items-center justify-between gap-2 ${Icon ? 'pl-8' : ''} ${disabled ? 'opacity-50 cursor-not-allowed' : ''}`}
      >
        <span className={`truncate ${selected?.value ? 'text-foreground' : 'text-muted-foreground'}`}>
          {selected?.label || placeholder}
        </span>
        <ChevronDown className={`h-3.5 w-3.5 text-muted-foreground flex-shrink-0 transition-transform ${open ? 'rotate-180' : ''}`} />
      </button>
      {open && !disabled && (
        <div className="absolute left-0 right-0 top-full mt-1 z-50 bg-card border border-border rounded-lg shadow-lg max-h-52 overflow-y-auto">
          {options.map(opt => (
            <button
              key={opt.value}
              className={`flex items-center w-full px-3 py-2 text-sm hover:bg-muted transition-colors text-left ${opt.value === value ? 'text-primary font-medium bg-primary/5' : 'text-foreground'}`}
              onMouseDown={e => e.preventDefault()}
              onClick={() => { onChange(opt.value); setOpen(false); }}
            >
              {opt.label}
            </button>
          ))}
        </div>
      )}
    </div>
  );
}

const FILTER_KEY = 'doc-list-filters';
function loadFilters() {
  try { const r = sessionStorage.getItem(FILTER_KEY); if (r) return JSON.parse(r); } catch {}
  return {};
}

export default function DocumentListPage() {
  const saved = useMemo(loadFilters, []);
  const [page, setPage] = useState<number>(saved.page ?? 1);
  const [status, setStatus] = useState<string>(saved.status ?? '');
  const [docTypeId, setDocTypeId] = useState<string>(saved.docTypeId ?? '');
  const [filterBranchId, setFilterBranchId] = useState<string>(saved.filterBranchId ?? '');
  const [search, setSearch] = useState<string>(saved.search ?? '');
  const [searchInput, setSearchInput] = useState<string>(saved.search ?? '');
  const [groupByVendor, setGroupByVendor] = useState<boolean>(saved.groupByVendor ?? false);
  const [confirmDeleteId, setConfirmDeleteId] = useState<string | null>(null);
  const [downloadingId, setDownloadingId] = useState<string | null>(null);
  const [previewingId, setPreviewingId] = useState<string | null>(null);
  // Mobile "..." menu — tracks which card's menu is open
  const [openMenuId, setOpenMenuId] = useState<string | null>(null);
  const menuRef = useRef<HTMLDivElement>(null);

  // Persist filters to sessionStorage whenever they change
  useEffect(() => {
    sessionStorage.setItem(FILTER_KEY, JSON.stringify(
      { page, status, docTypeId, filterBranchId, search, groupByVendor }
    ));
  }, [page, status, docTypeId, filterBranchId, search, groupByVendor]);

  // Close "..." menu when clicking outside
  useEffect(() => {
    const handler = (e: MouseEvent) => {
      if (menuRef.current && !menuRef.current.contains(e.target as Node)) {
        setOpenMenuId(null);
      }
    };
    if (openMenuId) document.addEventListener('mousedown', handler);
    return () => document.removeEventListener('mousedown', handler);
  }, [openMenuId]);

  const handlePreview = async (docId: string) => {
    setOpenMenuId(null);
    setPreviewingId(docId);
    try {
      const res = await documentApi.getSignedUrl(docId);
      window.open(res.data.url, '_blank');
    } finally {
      setPreviewingId(null);
    }
  };

  const handleDownload = async (docId: string, filename: string) => {
    setOpenMenuId(null);
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

  const { isAdmin, isManagerOrAbove } = useAuth();
  const queryClient = useQueryClient();

  const { data, isLoading } = useQuery<PagedResult<Document>>({
    queryKey: ['documents', page, status, search, docTypeId, filterBranchId],
    queryFn: () => documentApi.list({
      page,
      pageSize: groupByVendor ? 100 : 20,
      status: status || undefined,
      search: search || undefined,
      documentTypeId: docTypeId || undefined,
      filterBranchId: filterBranchId || undefined,
    }).then(r => r.data),
    refetchInterval: 30000,
  });

  // Document types for filter
  const { data: docTypes } = useQuery<DocumentType[]>({
    queryKey: ['document-types'],
    queryFn: () => configApi.getDocumentTypes().then(r => r.data),
    staleTime: 60_000,
  });

  // Branches for filter (managers and admins only)
  const { data: branches } = useQuery<Branch[]>({
    queryKey: ['branches'],
    queryFn: () => branchApi.list().then(r => r.data),
    staleTime: 300_000,
    enabled: isManagerOrAbove,
  });

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
    const groups: Record<string, { label: string; docs: Document[] }> = {};
    for (const doc of data.items) {
      const raw = doc.vendorName?.trim() || '';
      const key = raw ? raw.toLowerCase() : '__no_vendor__';
      if (!groups[key]) groups[key] = { label: raw || 'No Vendor Assigned', docs: [] };
      groups[key].docs.push(doc);
    }
    return Object.entries(groups)
      .sort(([a, { docs: da }], [b, { docs: db }]) => {
        if (a === '__no_vendor__') return -1;
        if (b === '__no_vendor__') return 1;
        const latestA = Math.max(...da.map(d => new Date(d.uploadedAt).getTime()));
        const latestB = Math.max(...db.map(d => new Date(d.uploadedAt).getTime()));
        return latestB - latestA;
      })
      .map(([, { label, docs }]) => [label, docs] as [string, Document[]]);
  }, [groupByVendor, data?.items]);

  // When grouped, the vendor column is redundant (vendor shown in group header row).
  const colSpan = groupByVendor ? (isAdmin ? 7 : 6) : (isAdmin ? 8 : 7);

  // Fixed column widths applied via <colgroup> so all tables (and all vendor-group
  // sub-tables) share identical column proportions regardless of row content.
  const ColGroup = () => (
    <colgroup>
      {!groupByVendor && <col style={{ width: '14%' }} />}
      <col style={{ width: groupByVendor ? '34%' : '28%' }} />
      <col style={{ width: '14%' }} />
      <col style={{ width: '10%' }} />
      <col style={{ width: '12%' }} />
      <col style={{ width: '10%' }} />
      <col style={{ width: '8%' }} />
      {isAdmin && <col style={{ width: '4%' }} />}
    </colgroup>
  );

  const DocRow = ({ doc }: { doc: Document }) => (
    <tr key={doc.id} className="hover:bg-muted/30">
      {!groupByVendor && (
        <td className="px-4 py-3">
          {doc.vendorName
            ? <span className="font-medium text-foreground">{doc.vendorName}</span>
            : <span className="text-muted-foreground">—</span>}
        </td>
      )}
      <td className="px-4 py-3">
        <div className="flex items-center gap-1.5">
          <Link to={`/documents/${doc.id}`} className="text-primary hover:underline font-medium">
            {doc.originalFilename}
          </Link>
          {doc.reuploadRequired && (
            <span title="Re-upload required">
              <AlertTriangle className="h-3.5 w-3.5 text-amber-500 flex-shrink-0" />
            </span>
          )}
          {doc.isValidating && (
            <span title="Validation in progress">
              <Loader2 className="h-3.5 w-3.5 text-blue-500 animate-spin flex-shrink-0" />
            </span>
          )}
        </div>
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
        {!groupByVendor && <th className="px-4 py-3 text-left font-medium text-muted-foreground">Vendor</th>}
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

  const MobileCard = ({ doc }: { doc: Document }) => (
    <div className="card overflow-visible relative">
      <div className="flex items-start gap-2 p-3">
        <Link to={`/documents/${doc.id}`} className="flex-1 min-w-0">
          <div className="flex items-center gap-1.5">
            <p className="text-sm font-medium text-foreground leading-snug truncate">{doc.originalFilename}</p>
            {doc.reuploadRequired && (
              <span title="Re-upload required"><AlertTriangle className="h-3.5 w-3.5 text-amber-500 flex-shrink-0" /></span>
            )}
            {doc.isValidating && (
              <span title="Validation in progress">
                <Loader2 className="h-3.5 w-3.5 text-blue-500 animate-spin flex-shrink-0" />
              </span>
            )}
          </div>
          <div className="flex items-center gap-1.5 mt-1 flex-wrap">
            <StatusBadge status={doc.status} />
            {doc.documentTypeName && (
              <span className="text-xs text-muted-foreground">{doc.documentTypeName}</span>
            )}
          </div>
          <p className="text-xs text-muted-foreground mt-0.5">
            {doc.vendorName ? <span className="text-blue-600">{doc.vendorName} · </span> : null}
            {new Date(doc.uploadedAt).toLocaleDateString()}
          </p>
        </Link>
        <div className="relative flex-shrink-0">
          <button
            onClick={e => { e.stopPropagation(); setOpenMenuId(openMenuId === doc.id ? null : doc.id); }}
            className="p-1.5 rounded-md text-muted-foreground hover:bg-muted hover:text-foreground transition-colors"
          >
            {(previewingId === doc.id || downloadingId === doc.id)
              ? <Loader2 className="h-4 w-4 animate-spin" />
              : <MoreVertical className="h-4 w-4" />}
          </button>
          {openMenuId === doc.id && (
            <div className="absolute right-0 top-full mt-1 z-50 bg-card border border-border rounded-lg shadow-lg min-w-[140px] py-1 overflow-hidden">
              <button onClick={() => handlePreview(doc.id)} className="flex items-center gap-2.5 w-full px-3 py-2 text-sm text-foreground hover:bg-muted transition-colors">
                <Eye className="h-4 w-4 text-muted-foreground" /> Preview
              </button>
              <button onClick={() => handleDownload(doc.id, doc.originalFilename)} className="flex items-center gap-2.5 w-full px-3 py-2 text-sm text-foreground hover:bg-muted transition-colors">
                <Download className="h-4 w-4 text-muted-foreground" /> Download
              </button>
              {isAdmin && (
                <>
                  <div className="border-t border-border my-1" />
                  <button onClick={() => { setOpenMenuId(null); setConfirmDeleteId(doc.id); }} className="flex items-center gap-2.5 w-full px-3 py-2 text-sm text-destructive hover:bg-red-50 transition-colors">
                    <Trash2 className="h-4 w-4" /> Delete
                  </button>
                </>
              )}
            </div>
          )}
        </div>
      </div>
    </div>
  );

  const activeFiltersCount = [status, search, docTypeId, filterBranchId].filter(Boolean).length;

  return (
    <div className="space-y-4">
      {/* Header */}
      <div className="flex items-center justify-between">
        <h1 className="text-xl sm:text-2xl font-bold text-foreground">Documents</h1>
        <Link to="/documents/upload" className="btn-primary flex items-center gap-2 text-sm">
          <Upload className="h-4 w-4" />
          Upload
        </Link>
      </div>

      {/* ── Filters ──────────────────────────────────────────────────────── */}
      <div className="card p-3 sm:p-4 space-y-3">
        {/* Search — full width on all screens */}
        <div className="relative">
          <Search className="absolute left-3 top-1/2 -translate-y-1/2 h-4 w-4 text-muted-foreground" />
          <input
            className="input pl-9 w-full"
            placeholder="Search by filename or vendor..."
            value={searchInput}
            onChange={e => setSearchInput(e.target.value)}
          />
        </div>

        {/* Filter row */}
        <div className={`grid gap-2 ${isManagerOrAbove ? 'grid-cols-2 sm:grid-cols-3' : 'grid-cols-2'}`}>
          {/* Branch filter — managers and admins only */}
          {isManagerOrAbove && (
            <CustomSelect
              value={filterBranchId}
              onChange={v => { setFilterBranchId(v); setPage(1); }}
              placeholder="All branches"
              icon={Building2}
              options={[
                { value: '', label: 'All branches' },
                ...(branches ?? []).map(b => ({ value: b.id, label: b.branchName })),
              ]}
            />
          )}

          {/* Status */}
          <CustomSelect
            value={status}
            onChange={v => { setStatus(v); setPage(1); }}
            placeholder="All statuses"
            options={STATUS_OPTIONS.map(s => ({ value: s, label: s || 'All statuses' }))}
          />

          {/* Document type */}
          <CustomSelect
            value={docTypeId}
            onChange={v => { setDocTypeId(v); setPage(1); }}
            placeholder="All types"
            icon={FileType}
            disabled={(docTypes?.length ?? 0) === 0}
            options={[
              { value: '', label: 'All types' },
              ...(docTypes ?? []).map(dt => ({ value: dt.id, label: dt.displayName })),
            ]}
          />
        </div>

        {/* Group toggle + active filter chips */}
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
            <div className="flex items-center gap-1.5 flex-wrap">
              {filterBranchId && (
                <span className="inline-flex items-center gap-1 px-2 py-0.5 rounded-full text-xs bg-primary/10 text-primary">
                  {branches?.find(b => b.id === filterBranchId)?.branchName ?? 'Branch'}
                  <button onClick={() => setFilterBranchId('')}><X className="h-3 w-3" /></button>
                </span>
              )}
              {status && (
                <span className="inline-flex items-center gap-1 px-2 py-0.5 rounded-full text-xs bg-primary/10 text-primary">
                  {status}
                  <button onClick={() => setStatus('')}><X className="h-3 w-3" /></button>
                </span>
              )}
              {docTypeId && (
                <span className="inline-flex items-center gap-1 px-2 py-0.5 rounded-full text-xs bg-primary/10 text-primary">
                  {docTypes?.find(dt => dt.id === docTypeId)?.displayName ?? 'Type'}
                  <button onClick={() => setDocTypeId('')}><X className="h-3 w-3" /></button>
                </span>
              )}
              {search && (
                <span className="inline-flex items-center gap-1 px-2 py-0.5 rounded-full text-xs bg-primary/10 text-primary">
                  "{search}"
                  <button onClick={() => { setSearch(''); setSearchInput(''); }}><X className="h-3 w-3" /></button>
                </span>
              )}
            </div>
          )}
        </div>
      </div>

      {/* ── Desktop table ────────────────────────────────────────────────── */}
      <div className="card hidden md:block overflow-x-auto">
        {groupByVendor && groupedItems ? (
          // Single table so all vendor groups share identical fixed column widths.
          <table className="w-full text-sm" style={{ tableLayout: 'fixed' }}>
            <ColGroup />
            <TableHead />
            <tbody className="divide-y divide-border">
              {groupedItems.map(([vendorGroup, docs]) => (
                <>
                  <tr key={`hdr-${vendorGroup}`} className="bg-muted/60 border-b border-border">
                    <td colSpan={colSpan} className="px-4 py-2">
                      <span className="flex items-center gap-2">
                        <Building2 className="h-4 w-4 text-muted-foreground flex-shrink-0" />
                        <span className="text-sm font-semibold text-foreground">{vendorGroup}</span>
                        <span className="text-xs text-muted-foreground">({docs.length})</span>
                      </span>
                    </td>
                  </tr>
                  {docs.map(doc => <DocRow key={doc.id} doc={doc} />)}
                </>
              ))}
            </tbody>
          </table>
        ) : (
          <table className="w-full text-sm" style={{ tableLayout: 'fixed' }}>
            <ColGroup />
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

      {/* ── Mobile card list ─────────────────────────────────────────────── */}
      <div className="md:hidden space-y-2" ref={menuRef}>
        {isLoading && (
          <div className="text-center py-8 text-muted-foreground text-sm">Loading...</div>
        )}
        {!isLoading && data?.items.length === 0 && (
          <div className="text-center py-8 text-muted-foreground text-sm">No documents found.</div>
        )}

        {groupByVendor && groupedItems
          ? groupedItems.map(([vendorGroup, docs]) => (
              <div key={vendorGroup} className="mb-3">
                <div className="flex items-center gap-2 px-3 py-1.5 bg-muted/60 rounded-lg border border-border mb-1">
                  <Building2 className="h-3.5 w-3.5 text-muted-foreground flex-shrink-0" />
                  <span className="text-xs font-semibold text-foreground truncate">{vendorGroup}</span>
                  <span className="text-xs text-muted-foreground ml-auto flex-shrink-0">({docs.length})</span>
                </div>
                <div className="space-y-2">
                  {docs.map(doc => <MobileCard key={doc.id} doc={doc} />)}
                </div>
              </div>
            ))
          : data?.items.map(doc => <MobileCard key={doc.id} doc={doc} />)
        }
      </div>

      {/* Delete confirmation modal */}
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

      {/* Pagination */}
      {data && data.totalPages > 1 && (
        <div className="flex items-center justify-between">
          <p className="text-sm text-muted-foreground">
            {(() => {
              const ps = groupByVendor ? 100 : 20;
              return `Showing ${(page - 1) * ps + 1}–${Math.min(page * ps, data.totalCount)} of ${data.totalCount}`;
            })()}
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
