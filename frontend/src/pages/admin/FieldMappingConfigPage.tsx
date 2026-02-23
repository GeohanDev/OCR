import { useState } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { configApi } from '../../api/client';
import type { DocumentType, FieldMappingConfig } from '../../types';
import { Plus, Trash2, Edit2, Loader2, Save, X } from 'lucide-react';

interface FieldFormData {
  fieldName: string;
  displayLabel: string;
  regexPattern: string;
  keywordAnchor: string;
  isRequired: boolean;
  erpMappingKey: string;
  confidenceThreshold: number;
  displayOrder: number;
}

const EMPTY_FORM: FieldFormData = {
  fieldName: '',
  displayLabel: '',
  regexPattern: '',
  keywordAnchor: '',
  isRequired: false,
  erpMappingKey: '',
  confidenceThreshold: 0.75,
  displayOrder: 0,
};

const ERP_KEYS = ['', 'VendorID', 'CurrencyID', 'BranchID', 'PurchaseOrderID'];

export default function FieldMappingConfigPage() {
  const queryClient = useQueryClient();
  const [selectedTypeId, setSelectedTypeId] = useState('');
  const [showForm, setShowForm] = useState(false);
  const [editingId, setEditingId] = useState<string | null>(null);
  const [form, setForm] = useState<FieldFormData>(EMPTY_FORM);

  const { data: docTypes } = useQuery<DocumentType[]>({
    queryKey: ['document-types'],
    queryFn: () => configApi.getDocumentTypes().then(r => r.data),
  });

  const { data: fields, isLoading } = useQuery<FieldMappingConfig[]>({
    queryKey: ['field-mappings', selectedTypeId],
    queryFn: () => configApi.getFieldMappings(selectedTypeId).then(r => r.data),
    enabled: !!selectedTypeId,
  });

  const createField = useMutation({
    mutationFn: (data: FieldFormData) => configApi.createFieldMapping(selectedTypeId, data),
    onSuccess: () => { queryClient.invalidateQueries({ queryKey: ['field-mappings', selectedTypeId] }); closeForm(); },
  });

  const updateField = useMutation({
    mutationFn: ({ id, data }: { id: string; data: FieldFormData }) =>
      configApi.updateFieldMapping(selectedTypeId, id, data),
    onSuccess: () => { queryClient.invalidateQueries({ queryKey: ['field-mappings', selectedTypeId] }); closeForm(); },
  });

  const deleteField = useMutation({
    mutationFn: (fieldId: string) => configApi.deleteFieldMapping(selectedTypeId, fieldId),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['field-mappings', selectedTypeId] }),
  });

  const openCreate = () => { setForm(EMPTY_FORM); setEditingId(null); setShowForm(true); };
  const openEdit = (f: FieldMappingConfig) => {
    setForm({
      fieldName: f.fieldName,
      displayLabel: f.displayLabel ?? '',
      regexPattern: f.regexPattern ?? '',
      keywordAnchor: f.keywordAnchor ?? '',
      isRequired: f.isRequired,
      erpMappingKey: f.erpMappingKey ?? '',
      confidenceThreshold: f.confidenceThreshold,
      displayOrder: f.displayOrder,
    });
    setEditingId(f.id);
    setShowForm(true);
  };
  const closeForm = () => { setShowForm(false); setEditingId(null); setForm(EMPTY_FORM); };

  const handleSubmit = () => {
    if (editingId) updateField.mutate({ id: editingId, data: form });
    else createField.mutate(form);
  };

  const isSaving = createField.isPending || updateField.isPending;

  return (
    <div className="space-y-5">
      <h1 className="text-2xl font-bold text-gray-900">Field Mapping Config</h1>

      <div className="flex flex-col sm:flex-row gap-3 items-start sm:items-center">
        <select
          className="input w-auto"
          value={selectedTypeId}
          onChange={e => setSelectedTypeId(e.target.value)}
        >
          <option value="">— Select document type —</option>
          {docTypes?.map(dt => <option key={dt.id} value={dt.id}>{dt.displayName}</option>)}
        </select>
        {selectedTypeId && (
          <button onClick={openCreate} className="btn-primary flex items-center gap-2 text-sm">
            <Plus className="h-4 w-4" /> Add Field
          </button>
        )}
      </div>

      {selectedTypeId && (
        <div className="card overflow-hidden">
          <table className="w-full text-sm">
            <thead className="bg-gray-50 border-b border-gray-200">
              <tr>
                <th className="px-4 py-3 text-left font-medium text-gray-500">#</th>
                <th className="px-4 py-3 text-left font-medium text-gray-500">Field Name</th>
                <th className="px-4 py-3 text-left font-medium text-gray-500">Regex</th>
                <th className="px-4 py-3 text-left font-medium text-gray-500">ERP Key</th>
                <th className="px-4 py-3 text-left font-medium text-gray-500">Required</th>
                <th className="px-4 py-3 text-left font-medium text-gray-500">Threshold</th>
                <th className="px-4 py-3 text-right font-medium text-gray-500">Actions</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-gray-100">
              {isLoading && (
                <tr><td colSpan={7} className="px-4 py-8 text-center text-gray-500">Loading...</td></tr>
              )}
              {fields?.map(f => (
                <tr key={f.id} className="hover:bg-gray-50">
                  <td className="px-4 py-3 text-gray-400">{f.displayOrder}</td>
                  <td className="px-4 py-3">
                    <p className="font-medium text-gray-900">{f.fieldName}</p>
                    {f.displayLabel && <p className="text-xs text-gray-500">{f.displayLabel}</p>}
                  </td>
                  <td className="px-4 py-3 text-gray-500 font-mono text-xs truncate max-w-xs">
                    {f.regexPattern ?? '—'}
                  </td>
                  <td className="px-4 py-3">
                    {f.erpMappingKey ? (
                      <span className="badge bg-blue-100 text-blue-700 text-xs">{f.erpMappingKey}</span>
                    ) : '—'}
                  </td>
                  <td className="px-4 py-3">
                    {f.isRequired
                      ? <span className="badge bg-orange-100 text-orange-700 text-xs">Required</span>
                      : <span className="text-gray-400">Optional</span>}
                  </td>
                  <td className="px-4 py-3 text-gray-600">{Math.round(f.confidenceThreshold * 100)}%</td>
                  <td className="px-4 py-3 text-right">
                    <div className="flex items-center justify-end gap-2">
                      <button onClick={() => openEdit(f)} className="text-gray-400 hover:text-gray-700">
                        <Edit2 className="h-4 w-4" />
                      </button>
                      <button
                        onClick={() => deleteField.mutate(f.id)}
                        disabled={deleteField.isPending}
                        className="text-gray-400 hover:text-red-600"
                      >
                        <Trash2 className="h-4 w-4" />
                      </button>
                    </div>
                  </td>
                </tr>
              ))}
              {!isLoading && fields?.length === 0 && (
                <tr><td colSpan={7} className="px-4 py-8 text-center text-gray-500">No fields configured.</td></tr>
              )}
            </tbody>
          </table>
        </div>
      )}

      {/* Modal */}
      {showForm && (
        <div className="fixed inset-0 z-50 flex items-center justify-center p-4 bg-black/50">
          <div className="card p-6 w-full max-w-lg space-y-4 max-h-screen overflow-y-auto">
            <div className="flex items-center justify-between">
              <h3 className="font-semibold text-gray-900">{editingId ? 'Edit Field' : 'Add Field'}</h3>
              <button onClick={closeForm}><X className="h-5 w-5 text-gray-400" /></button>
            </div>

            <div className="space-y-3">
              <Field label="Field Name *">
                <input className="input" value={form.fieldName} onChange={e => setForm(f => ({ ...f, fieldName: e.target.value }))} />
              </Field>
              <Field label="Display Label">
                <input className="input" value={form.displayLabel} onChange={e => setForm(f => ({ ...f, displayLabel: e.target.value }))} />
              </Field>
              <Field label="Regex Pattern">
                <input className="input font-mono text-sm" placeholder="e.g. (?i)vendor\s*:\s*(\S+)" value={form.regexPattern} onChange={e => setForm(f => ({ ...f, regexPattern: e.target.value }))} />
              </Field>
              <Field label="Keyword Anchor">
                <input className="input" placeholder="e.g. VENDOR" value={form.keywordAnchor} onChange={e => setForm(f => ({ ...f, keywordAnchor: e.target.value }))} />
              </Field>
              <Field label="ERP Mapping Key">
                <select className="input" value={form.erpMappingKey} onChange={e => setForm(f => ({ ...f, erpMappingKey: e.target.value }))}>
                  {ERP_KEYS.map(k => <option key={k} value={k}>{k || '— None —'}</option>)}
                </select>
              </Field>
              <Field label={`Confidence Threshold: ${Math.round(form.confidenceThreshold * 100)}%`}>
                <input
                  type="range" min="0" max="100" step="5"
                  value={Math.round(form.confidenceThreshold * 100)}
                  onChange={e => setForm(f => ({ ...f, confidenceThreshold: Number(e.target.value) / 100 }))}
                  className="w-full"
                />
              </Field>
              <Field label="Display Order">
                <input type="number" className="input" value={form.displayOrder} onChange={e => setForm(f => ({ ...f, displayOrder: Number(e.target.value) }))} />
              </Field>
              <div className="flex items-center gap-2">
                <input
                  type="checkbox"
                  id="isRequired"
                  checked={form.isRequired}
                  onChange={e => setForm(f => ({ ...f, isRequired: e.target.checked }))}
                  className="rounded border-gray-300"
                />
                <label htmlFor="isRequired" className="text-sm text-gray-700">Required field (blocks approval if missing)</label>
              </div>
            </div>

            <div className="flex justify-end gap-3 pt-2">
              <button className="btn-secondary" onClick={closeForm}>Cancel</button>
              <button
                className="btn-primary flex items-center gap-2"
                onClick={handleSubmit}
                disabled={isSaving || !form.fieldName.trim()}
              >
                {isSaving ? <Loader2 className="h-4 w-4 animate-spin" /> : <Save className="h-4 w-4" />}
                {editingId ? 'Update' : 'Create'}
              </button>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}

function Field({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <div>
      <label className="block text-sm font-medium text-gray-700 mb-1">{label}</label>
      {children}
    </div>
  );
}
