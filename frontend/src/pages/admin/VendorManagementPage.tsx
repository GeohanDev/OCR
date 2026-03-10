import { useState, useEffect } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { vendorApi } from '../../api/client';
import { Search, RefreshCw, ChevronLeft, ChevronRight, Loader2, Building2 } from 'lucide-react';
import type { PagedResult, Vendor } from '../../types';

export default function VendorManagementPage() {
  const [page, setPage] = useState(1);
  const [search, setSearch] = useState('');
  const [searchInput, setSearchInput] = useState('');
  const [syncMsg, setSyncMsg] = useState<{ type: 'success' | 'error'; text: string } | null>(null);
  const queryClient = useQueryClient();

  useEffect(() => {
    const t = setTimeout(() => { setSearch(searchInput); setPage(1); }, 400);
    return () => clearTimeout(t);
  }, [searchInput]);

  const { data, isLoading } = useQuery<PagedResult<Vendor>>({
    queryKey: ['vendors', page, search],
    queryFn: () => vendorApi.list({ page, pageSize: 50, search: search || undefined }).then(r => r.data),
  });

  const syncMutation = useMutation({
    mutationFn: () => vendorApi.sync(),
    onSuccess: (res) => {
      queryClient.invalidateQueries({ queryKey: ['vendors'] });
      setSyncMsg({ type: 'success', text: `Sync complete — ${res.data.syncedCount} vendors updated.` });
      setTimeout(() => setSyncMsg(null), 5000);
    },
    onError: (err: unknown) => {
      const msg = (err as { response?: { data?: { message?: string } } })?.response?.data?.message ?? 'Sync failed.';
      setSyncMsg({ type: 'error', text: msg });
    },
  });

  return (
    <div className="space-y-4">
      <div className="flex items-center justify-between">
        <div>
          <h1 className="text-2xl font-bold text-foreground">Vendor Management</h1>
          <p className="text-sm text-muted-foreground mt-1">
            Local vendor list synced from Acumatica. {data?.totalCount ?? 0} vendors.
          </p>
        </div>
        <button
          onClick={() => syncMutation.mutate()}
          disabled={syncMutation.isPending}
          className="btn-primary flex items-center gap-2"
        >
          {syncMutation.isPending
            ? <Loader2 className="h-4 w-4 animate-spin" />
            : <RefreshCw className="h-4 w-4" />}
          Sync from Acumatica
        </button>
      </div>

      {syncMsg && (
        <div className={`rounded-md px-4 py-3 text-sm ${
          syncMsg.type === 'success'
            ? 'bg-green-50 border border-green-200 text-green-800'
            : 'bg-red-50 border border-red-200 text-red-800'
        }`}>
          {syncMsg.text}
        </div>
      )}

      {/* Search */}
      <div className="relative max-w-sm">
        <Search className="absolute left-3 top-1/2 -translate-y-1/2 h-4 w-4 text-muted-foreground" />
        <input
          className="input pl-9"
          placeholder="Search vendor name or ID..."
          value={searchInput}
          onChange={e => setSearchInput(e.target.value)}
        />
      </div>

      {/* Table */}
      <div className="card overflow-hidden">
        <table className="w-full text-sm">
          <thead className="bg-muted/50 border-b border-border">
            <tr>
              <th className="px-4 py-3 text-left font-medium text-muted-foreground">Vendor ID</th>
              <th className="px-4 py-3 text-left font-medium text-muted-foreground">Vendor Name</th>
              <th className="px-4 py-3 text-left font-medium text-muted-foreground">Address</th>
              <th className="px-4 py-3 text-left font-medium text-muted-foreground">Terms</th>
              <th className="px-4 py-3 text-left font-medium text-muted-foreground">Status</th>
              <th className="px-4 py-3 text-left font-medium text-muted-foreground">Last Synced</th>
            </tr>
          </thead>
          <tbody className="divide-y divide-border">
            {isLoading && (
              <tr><td colSpan={6} className="px-4 py-8 text-center text-muted-foreground">Loading...</td></tr>
            )}
            {data?.items.map(v => (
              <tr key={v.id} className="hover:bg-muted/30">
                <td className="px-4 py-3 font-mono text-xs text-muted-foreground">{v.acumaticaVendorId}</td>
                <td className="px-4 py-3 font-medium text-foreground">
                  <div className="flex items-center gap-2">
                    <Building2 className="h-4 w-4 text-muted-foreground flex-shrink-0" />
                    {v.vendorName}
                  </div>
                </td>
                <td className="px-4 py-3 text-muted-foreground text-xs">
                  {[v.addressLine1, v.addressLine2, v.city, v.state, v.postalCode, v.country]
                    .filter(Boolean).join(', ') || '—'}
                </td>
                <td className="px-4 py-3 text-muted-foreground">{v.paymentTerms || '—'}</td>
                <td className="px-4 py-3">
                  <span className={`inline-flex items-center px-2 py-0.5 rounded-full text-xs font-medium ${
                    v.isActive
                      ? 'bg-green-100 text-green-700'
                      : 'bg-gray-100 text-gray-500'
                  }`}>
                    {v.isActive ? 'Active' : 'Inactive'}
                  </span>
                </td>
                <td className="px-4 py-3 text-muted-foreground text-xs">
                  {new Date(v.lastSyncedAt).toLocaleString()}
                </td>
              </tr>
            ))}
            {!isLoading && data?.items.length === 0 && (
              <tr>
                <td colSpan={6} className="px-4 py-12 text-center text-muted-foreground">
                  No vendors found. Click "Sync from Acumatica" to import vendors.
                </td>
              </tr>
            )}
          </tbody>
        </table>
      </div>

      {/* Pagination */}
      {data && data.totalPages > 1 && (
        <div className="flex items-center justify-between">
          <p className="text-sm text-muted-foreground">
            Showing {(page - 1) * 50 + 1}–{Math.min(page * 50, data.totalCount)} of {data.totalCount}
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
