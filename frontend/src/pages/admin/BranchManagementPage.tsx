import { useState } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { branchApi } from '../../api/client';
import { RefreshCw, Loader2, GitBranch, ChevronDown, ChevronUp, Code } from 'lucide-react';

interface Branch {
  id: string;
  branchCode: string;
  branchName: string;
  acumaticaBranchId: string;
  syncedAt: string;
}

export default function BranchManagementPage() {
  const queryClient = useQueryClient();
  const [syncMsg, setSyncMsg] = useState<{ type: 'success' | 'error'; text: string } | null>(null);
  const [expandedCode, setExpandedCode] = useState<string | null>(null);
  const [erpData, setErpData] = useState<Record<string, unknown> | null>(null);
  const [erpLoading, setErpLoading] = useState(false);

  const { data: branches, isLoading } = useQuery<Branch[]>({
    queryKey: ['branches-admin'],
    queryFn: () => branchApi.list().then(r => r.data),
  });

  const syncMutation = useMutation({
    mutationFn: () => branchApi.sync(),
    onSuccess: (res) => {
      queryClient.invalidateQueries({ queryKey: ['branches-admin'] });
      queryClient.invalidateQueries({ queryKey: ['branches'] });
      setSyncMsg({ type: 'success', text: `Sync complete — ${res.data.syncedCount} branches updated.` });
      setTimeout(() => setSyncMsg(null), 5000);
    },
    onError: () => {
      setSyncMsg({ type: 'error', text: 'Sync failed. Check ERP connection.' });
    },
  });

  const toggleErp = async (branch: Branch) => {
    if (expandedCode === branch.branchCode) {
      setExpandedCode(null);
      setErpData(null);
      return;
    }
    setExpandedCode(branch.branchCode);
    setErpData(null);
    setErpLoading(true);
    try {
      const res = await branchApi.getErpData(branch.branchCode);
      setErpData(res.data as Record<string, unknown>);
    } catch {
      setErpData({ error: 'Failed to load ERP data for this branch.' });
    } finally {
      setErpLoading(false);
    }
  };

  return (
    <div className="space-y-4">
      <div className="flex items-center justify-between">
        <div>
          <h1 className="text-2xl font-bold text-foreground">Branch Management</h1>
          <p className="text-sm text-muted-foreground mt-1">
            Branches synced from Acumatica. {branches?.length ?? 0} branches.
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

      <div className="card overflow-hidden">
        <table className="w-full text-sm">
          <thead className="bg-muted/50 border-b border-border">
            <tr>
              <th className="px-4 py-3 text-left font-medium text-muted-foreground">Branch Name</th>
              <th className="px-4 py-3 text-left font-medium text-muted-foreground">Branch Code</th>
              <th className="px-4 py-3 text-left font-medium text-muted-foreground">Acumatica ID</th>
              <th className="px-4 py-3 text-left font-medium text-muted-foreground hidden md:table-cell">Last Synced</th>
              <th className="px-4 py-3 text-right font-medium text-muted-foreground">ERP Data</th>
            </tr>
          </thead>
          <tbody className="divide-y divide-border">
            {isLoading && (
              <tr><td colSpan={5} className="px-4 py-8 text-center text-muted-foreground">
                <Loader2 className="h-4 w-4 animate-spin inline mr-2" />Loading...
              </td></tr>
            )}
            {!isLoading && (branches?.length ?? 0) === 0 && (
              <tr><td colSpan={5} className="px-4 py-8 text-center text-muted-foreground">
                No branches found. Click "Sync from Acumatica" to import branches.
              </td></tr>
            )}
            {branches?.map(branch => (
              <>
                <tr key={branch.id} className="hover:bg-muted/30">
                  <td className="px-4 py-3">
                    <div className="flex items-center gap-2">
                      <GitBranch className="h-4 w-4 text-muted-foreground flex-shrink-0" />
                      <span className="font-medium text-foreground">{branch.branchName}</span>
                    </div>
                  </td>
                  <td className="px-4 py-3 font-mono text-xs text-muted-foreground">{branch.branchCode}</td>
                  <td className="px-4 py-3 font-mono text-xs text-muted-foreground">{branch.acumaticaBranchId}</td>
                  <td className="px-4 py-3 text-muted-foreground hidden md:table-cell">
                    {new Date(branch.syncedAt).toLocaleDateString()}
                  </td>
                  <td className="px-4 py-3 text-right">
                    <button
                      onClick={() => toggleErp(branch)}
                      className="flex items-center gap-1.5 text-xs text-primary hover:text-primary/80 ml-auto"
                      title="View raw ERP data"
                    >
                      <Code className="h-3.5 w-3.5" />
                      {expandedCode === branch.branchCode
                        ? <ChevronUp className="h-3.5 w-3.5" />
                        : <ChevronDown className="h-3.5 w-3.5" />}
                    </button>
                  </td>
                </tr>
                {expandedCode === branch.branchCode && (
                  <tr key={`${branch.id}-erp`}>
                    <td colSpan={5} className="px-4 pb-4 bg-muted/20">
                      {erpLoading ? (
                        <div className="flex items-center gap-2 text-sm text-muted-foreground py-2">
                          <Loader2 className="h-4 w-4 animate-spin" /> Loading ERP data…
                        </div>
                      ) : (
                        <pre className="text-xs bg-card border border-border rounded-md p-3 overflow-x-auto max-h-64 leading-relaxed">
                          {JSON.stringify(erpData, null, 2)}
                        </pre>
                      )}
                    </td>
                  </tr>
                )}
              </>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}
