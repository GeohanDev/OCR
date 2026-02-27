import { useState } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { Link } from 'react-router-dom';
import { documentApi } from '../api/client';
import StatusBadge from '../components/ui/StatusBadge';
import { useAuth } from '../contexts/AuthContext';
import type { PagedResult, Document } from '../types';
import { Upload, Search, ChevronLeft, ChevronRight, Trash2, Loader2 } from 'lucide-react';

const STATUS_OPTIONS = ['', 'Uploaded', 'Processing', 'PendingReview', 'ReviewInProgress', 'Approved', 'Rejected', 'Pushed'];

export default function DocumentListPage() {
  const [page, setPage] = useState(1);
  const [status, setStatus] = useState('');
  const [search, setSearch] = useState('');
  const [searchInput, setSearchInput] = useState('');
  const [confirmDeleteId, setConfirmDeleteId] = useState<string | null>(null);
  const { isAdmin } = useAuth();
  const queryClient = useQueryClient();

  const { data, isLoading } = useQuery<PagedResult<Document>>({
    queryKey: ['documents', page, status, search],
    queryFn: () => documentApi.list({ page, pageSize: 20, status: status || undefined, search: search || undefined })
      .then(r => r.data),
  });

  const deleteDoc = useMutation({
    mutationFn: (id: string) => documentApi.delete(id),
    onSuccess: () => {
      setConfirmDeleteId(null);
      queryClient.invalidateQueries({ queryKey: ['documents'] });
    },
  });

  return (
    <div className="space-y-4">
      <div className="flex items-center justify-between">
        <h1 className="text-2xl font-bold text-gray-900">Documents</h1>
        <Link to="/documents/upload" className="btn-primary flex items-center gap-2">
          <Upload className="h-4 w-4" />
          Upload
        </Link>
      </div>

      {/* Filters */}
      <div className="flex flex-col sm:flex-row gap-3">
        <div className="relative flex-1">
          <Search className="absolute left-3 top-1/2 -translate-y-1/2 h-4 w-4 text-gray-400" />
          <input
            className="input pl-9"
            placeholder="Search by filename..."
            value={searchInput}
            onChange={e => setSearchInput(e.target.value)}
            onKeyDown={e => { if (e.key === 'Enter') { setSearch(searchInput); setPage(1); } }}
          />
        </div>
        <select
          className="input w-auto"
          value={status}
          onChange={e => { setStatus(e.target.value); setPage(1); }}
        >
          {STATUS_OPTIONS.map(s => <option key={s} value={s}>{s || 'All statuses'}</option>)}
        </select>
      </div>

      {/* Table (desktop) */}
      <div className="card hidden md:block overflow-hidden">
        <table className="w-full text-sm">
          <thead className="bg-gray-50 border-b border-gray-200">
            <tr>
              <th className="px-4 py-3 text-left font-medium text-gray-500">Filename</th>
              <th className="px-4 py-3 text-left font-medium text-gray-500">Type</th>
              <th className="px-4 py-3 text-left font-medium text-gray-500">Status</th>
              <th className="px-4 py-3 text-left font-medium text-gray-500">Uploaded by</th>
              <th className="px-4 py-3 text-left font-medium text-gray-500">Date</th>
              {isAdmin && <th className="px-4 py-3" />}
            </tr>
          </thead>
          <tbody className="divide-y divide-gray-100">
            {isLoading && (
              <tr><td colSpan={isAdmin ? 6 : 5} className="px-4 py-8 text-center text-gray-500">Loading...</td></tr>
            )}
            {data?.items.map(doc => (
              <tr key={doc.id} className="hover:bg-gray-50">
                <td className="px-4 py-3">
                  <Link to={`/documents/${doc.id}`} className="text-blue-600 hover:underline font-medium">
                    {doc.originalFilename}
                  </Link>
                </td>
                <td className="px-4 py-3 text-gray-600">{doc.documentTypeName ?? '—'}</td>
                <td className="px-4 py-3"><StatusBadge status={doc.status} /></td>
                <td className="px-4 py-3 text-gray-600">{doc.uploadedByUsername}</td>
                <td className="px-4 py-3 text-gray-600">{new Date(doc.uploadedAt).toLocaleDateString()}</td>
                {isAdmin && (
                  <td className="px-4 py-3 text-right">
                    <button
                      onClick={() => setConfirmDeleteId(doc.id)}
                      className="text-gray-400 hover:text-red-600 transition-colors"
                      title="Delete document"
                    >
                      <Trash2 className="h-4 w-4" />
                    </button>
                  </td>
                )}
              </tr>
            ))}
            {!isLoading && data?.items.length === 0 && (
              <tr><td colSpan={isAdmin ? 6 : 5} className="px-4 py-8 text-center text-gray-500">No documents found.</td></tr>
            )}
          </tbody>
        </table>
      </div>

      {/* Card list (mobile) */}
      <div className="md:hidden space-y-3">
        {data?.items.map(doc => (
          <Link key={doc.id} to={`/documents/${doc.id}`} className="card p-4 block">
            <div className="flex items-start justify-between mb-2">
              <p className="text-sm font-medium text-gray-900 truncate pr-2">{doc.originalFilename}</p>
              <StatusBadge status={doc.status} />
            </div>
            <p className="text-xs text-gray-500">{doc.uploadedByUsername} · {new Date(doc.uploadedAt).toLocaleDateString()}</p>
          </Link>
        ))}
      </div>

      {/* Delete confirmation modal */}
      {confirmDeleteId && (
        <div className="fixed inset-0 z-50 flex items-center justify-center p-4 bg-black/50">
          <div className="card p-6 w-full max-w-sm space-y-4">
            <h3 className="font-semibold text-gray-900">Delete Document</h3>
            <p className="text-sm text-gray-600">
              Are you sure you want to delete "
              <span className="font-medium">
                {data?.items.find(d => d.id === confirmDeleteId)?.originalFilename}
              </span>
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
          <p className="text-sm text-gray-600">
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
