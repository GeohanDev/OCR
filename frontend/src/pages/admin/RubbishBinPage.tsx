import { useState } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { trashApi } from '../../api/client';
import type { TrashedDocument, TrashedFieldConfig, TrashedDocType } from '../../types';
import { Trash2, RotateCcw, FileText, Settings, Loader2, AlertTriangle } from 'lucide-react';
import { useAuth } from '../../contexts/AuthContext';

export default function RubbishBinPage() {
  const [tab, setTab] = useState<'documents' | 'config'>('documents');
  const queryClient = useQueryClient();
  const { isAdmin } = useAuth();

  const { data: trashedDocs, isLoading: docsLoading } = useQuery<TrashedDocument[]>({
    queryKey: ['trash-documents'],
    queryFn: () => trashApi.getTrashedDocuments().then(r => r.data),
  });

  const { data: trashedFields } = useQuery<TrashedFieldConfig[]>({
    queryKey: ['trash-field-mappings'],
    queryFn: () => trashApi.getTrashedFieldMappings().then(r => r.data),
    enabled: isAdmin,
  });

  const { data: trashedTypes } = useQuery<TrashedDocType[]>({
    queryKey: ['trash-doc-types'],
    queryFn: () => trashApi.getTrashedDocTypes().then(r => r.data),
    enabled: isAdmin,
  });

  const restoreDoc = useMutation({
    mutationFn: (id: string) => trashApi.restoreDocument(id),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['trash-documents'] });
      queryClient.invalidateQueries({ queryKey: ['documents'] });
    },
  });

  const restoreField = useMutation({
    mutationFn: (id: string) => trashApi.restoreFieldMapping(id),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['trash-field-mappings'] }),
  });

  const restoreType = useMutation({
    mutationFn: (id: string) => trashApi.restoreDocType(id),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['trash-doc-types'] });
      queryClient.invalidateQueries({ queryKey: ['trash-field-mappings'] });
      queryClient.invalidateQueries({ queryKey: ['document-types'] });
    },
  });

  const purge = useMutation({
    mutationFn: () => trashApi.purgeAll(),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['trash-documents'] });
      queryClient.invalidateQueries({ queryKey: ['trash-field-mappings'] });
      queryClient.invalidateQueries({ queryKey: ['trash-doc-types'] });
    },
  });

  const fmtDate = (iso: string) => new Date(iso).toLocaleDateString();
  const daysLeft = (iso: string) => {
    const deleted = new Date(iso);
    const purgeDate = new Date(deleted.getTime() + 30 * 24 * 60 * 60 * 1000);
    const days = Math.ceil((purgeDate.getTime() - Date.now()) / (1000 * 60 * 60 * 24));
    return days;
  };

  return (
    <div className="space-y-5">
      <div className="flex items-center justify-between">
        <div className="flex items-center gap-3">
          <Trash2 className="h-6 w-6 text-muted-foreground" />
          <h1 className="text-2xl font-bold text-foreground">Rubbish Bin</h1>
        </div>
        {isAdmin && (
          <button
            onClick={() => purge.mutate()}
            disabled={purge.isPending}
            className="flex items-center gap-2 text-sm text-destructive border border-red-200 hover:bg-red-50 rounded-md px-3 py-2 transition-colors"
          >
            {purge.isPending ? <Loader2 className="h-4 w-4 animate-spin" /> : <Trash2 className="h-4 w-4" />}
            Clear Expired Items
          </button>
        )}
      </div>

      <div className="flex items-center gap-1 text-xs text-muted-foreground bg-amber-50 border border-amber-200 rounded-lg px-3 py-2">
        <AlertTriangle className="h-3.5 w-3.5 text-amber-500 flex-shrink-0" />
        <span className="ml-1">Items in the rubbish bin are permanently deleted after 30 days.</span>
      </div>

      {/* Tab selector */}
      <div className="flex gap-1 border-b border-border">
        <button
          onClick={() => setTab('documents')}
          className={`flex items-center gap-2 px-4 py-2.5 text-sm font-medium border-b-2 transition-colors ${
            tab === 'documents' ? 'border-primary text-primary' : 'border-transparent text-muted-foreground hover:text-foreground'
          }`}
        >
          <FileText className="h-4 w-4" /> Documents
          {trashedDocs && trashedDocs.length > 0 && (
            <span className="ml-1 text-xs bg-muted px-1.5 py-0.5 rounded-full">{trashedDocs.length}</span>
          )}
        </button>
        {isAdmin && (
          <button
            onClick={() => setTab('config')}
            className={`flex items-center gap-2 px-4 py-2.5 text-sm font-medium border-b-2 transition-colors ${
              tab === 'config' ? 'border-primary text-primary' : 'border-transparent text-muted-foreground hover:text-foreground'
            }`}
          >
            <Settings className="h-4 w-4" /> Field Config
            {((trashedTypes?.length ?? 0) + (trashedFields?.length ?? 0)) > 0 && (
              <span className="ml-1 text-xs bg-muted px-1.5 py-0.5 rounded-full">
                {(trashedTypes?.length ?? 0) + (trashedFields?.length ?? 0)}
              </span>
            )}
          </button>
        )}
      </div>

      {/* Documents tab */}
      {tab === 'documents' && (
        <div className="card overflow-hidden">
          {docsLoading ? (
            <div className="px-4 py-8 text-center text-muted-foreground flex items-center justify-center gap-2">
              <Loader2 className="h-4 w-4 animate-spin" /> Loading...
            </div>
          ) : !trashedDocs?.length ? (
            <div className="px-4 py-12 text-center text-muted-foreground text-sm">No deleted documents.</div>
          ) : (
            <table className="w-full text-sm">
              <thead className="bg-muted/50 border-b border-border">
                <tr>
                  <th className="px-4 py-3 text-left font-medium text-muted-foreground">Filename</th>
                  <th className="px-4 py-3 text-left font-medium text-muted-foreground">Type</th>
                  <th className="px-4 py-3 text-left font-medium text-muted-foreground">Status</th>
                  <th className="px-4 py-3 text-left font-medium text-muted-foreground">Uploaded By</th>
                  <th className="px-4 py-3 text-left font-medium text-muted-foreground">Deleted</th>
                  <th className="px-4 py-3 text-left font-medium text-muted-foreground">Expires In</th>
                  <th className="px-4 py-3 text-right font-medium text-muted-foreground">Action</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-border">
                {trashedDocs.map(doc => {
                  const days = daysLeft(doc.deletedAt);
                  return (
                    <tr key={doc.id} className="hover:bg-muted/30">
                      <td className="px-4 py-3 font-medium text-foreground truncate max-w-[200px]">{doc.originalFilename}</td>
                      <td className="px-4 py-3 text-muted-foreground">{doc.documentTypeName ?? '—'}</td>
                      <td className="px-4 py-3">
                        <span className="text-xs px-2 py-0.5 rounded-full bg-muted text-muted-foreground">{doc.status}</span>
                      </td>
                      <td className="px-4 py-3 text-muted-foreground">{doc.uploadedByUsername}</td>
                      <td className="px-4 py-3 text-muted-foreground">{fmtDate(doc.deletedAt)}</td>
                      <td className="px-4 py-3">
                        <span className={`text-xs font-medium ${days <= 3 ? 'text-red-600' : days <= 7 ? 'text-amber-600' : 'text-muted-foreground'}`}>
                          {days > 0 ? `${days}d` : 'Expired'}
                        </span>
                      </td>
                      <td className="px-4 py-3 text-right">
                        <button
                          onClick={() => restoreDoc.mutate(doc.id)}
                          disabled={restoreDoc.isPending}
                          className="flex items-center gap-1.5 text-xs text-primary hover:text-primary/80 ml-auto"
                        >
                          <RotateCcw className="h-3.5 w-3.5" /> Restore
                        </button>
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          )}
        </div>
      )}

      {/* Field Config tab (Admin only) */}
      {tab === 'config' && isAdmin && (
        <div className="space-y-4">
          {/* Document Types */}
          <div className="card overflow-hidden">
            <div className="px-4 py-3 border-b border-border bg-muted/30">
              <p className="text-sm font-semibold text-foreground">Document Types</p>
            </div>
            {!trashedTypes?.length ? (
              <div className="px-4 py-6 text-center text-muted-foreground text-sm">No deleted document types.</div>
            ) : (
              <table className="w-full text-sm">
                <thead className="bg-muted/50 border-b border-border">
                  <tr>
                    <th className="px-4 py-3 text-left font-medium text-muted-foreground">Display Name</th>
                    <th className="px-4 py-3 text-left font-medium text-muted-foreground">Type Key</th>
                    <th className="px-4 py-3 text-left font-medium text-muted-foreground">Deleted</th>
                    <th className="px-4 py-3 text-left font-medium text-muted-foreground">Expires In</th>
                    <th className="px-4 py-3 text-right font-medium text-muted-foreground">Action</th>
                  </tr>
                </thead>
                <tbody className="divide-y divide-border">
                  {trashedTypes.map(dt => {
                    const days = daysLeft(dt.deletedAt);
                    return (
                      <tr key={dt.id} className="hover:bg-muted/30">
                        <td className="px-4 py-3 font-medium text-foreground">{dt.displayName}</td>
                        <td className="px-4 py-3 font-mono text-xs text-muted-foreground">{dt.typeKey}</td>
                        <td className="px-4 py-3 text-muted-foreground">{fmtDate(dt.deletedAt)}</td>
                        <td className="px-4 py-3">
                          <span className={`text-xs font-medium ${days <= 3 ? 'text-red-600' : days <= 7 ? 'text-amber-600' : 'text-muted-foreground'}`}>
                            {days > 0 ? `${days}d` : 'Expired'}
                          </span>
                        </td>
                        <td className="px-4 py-3 text-right">
                          <button
                            onClick={() => restoreType.mutate(dt.id)}
                            disabled={restoreType.isPending}
                            className="flex items-center gap-1.5 text-xs text-primary hover:text-primary/80 ml-auto"
                          >
                            <RotateCcw className="h-3.5 w-3.5" /> Restore
                          </button>
                        </td>
                      </tr>
                    );
                  })}
                </tbody>
              </table>
            )}
          </div>

          {/* Individual Field Configs */}
          <div className="card overflow-hidden">
            <div className="px-4 py-3 border-b border-border bg-muted/30">
              <p className="text-sm font-semibold text-foreground">Field Mappings</p>
            </div>
            {!trashedFields?.length ? (
              <div className="px-4 py-6 text-center text-muted-foreground text-sm">No deleted field mappings.</div>
            ) : (
              <table className="w-full text-sm">
                <thead className="bg-muted/50 border-b border-border">
                  <tr>
                    <th className="px-4 py-3 text-left font-medium text-muted-foreground">Field</th>
                    <th className="px-4 py-3 text-left font-medium text-muted-foreground">Document Type</th>
                    <th className="px-4 py-3 text-left font-medium text-muted-foreground">Deleted</th>
                    <th className="px-4 py-3 text-left font-medium text-muted-foreground">Expires In</th>
                    <th className="px-4 py-3 text-right font-medium text-muted-foreground">Action</th>
                  </tr>
                </thead>
                <tbody className="divide-y divide-border">
                  {trashedFields.map(f => {
                    const days = daysLeft(f.deletedAt);
                    return (
                      <tr key={f.id} className="hover:bg-muted/30">
                        <td className="px-4 py-3">
                          <p className="font-medium text-foreground">{f.fieldName}</p>
                          {f.displayLabel && <p className="text-xs text-muted-foreground">{f.displayLabel}</p>}
                        </td>
                        <td className="px-4 py-3 text-muted-foreground">{f.documentTypeName}</td>
                        <td className="px-4 py-3 text-muted-foreground">{fmtDate(f.deletedAt)}</td>
                        <td className="px-4 py-3">
                          <span className={`text-xs font-medium ${days <= 3 ? 'text-red-600' : days <= 7 ? 'text-amber-600' : 'text-muted-foreground'}`}>
                            {days > 0 ? `${days}d` : 'Expired'}
                          </span>
                        </td>
                        <td className="px-4 py-3 text-right">
                          <button
                            onClick={() => restoreField.mutate(f.id)}
                            disabled={restoreField.isPending}
                            className="flex items-center gap-1.5 text-xs text-primary hover:text-primary/80 ml-auto"
                          >
                            <RotateCcw className="h-3.5 w-3.5" /> Restore
                          </button>
                        </td>
                      </tr>
                    );
                  })}
                </tbody>
              </table>
            )}
          </div>
        </div>
      )}
    </div>
  );
}
