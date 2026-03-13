import { useState } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { configApi, erpApi } from '../../api/client';
import type { DocumentType, FieldMappingConfig, ErpEntity, DocumentCategory } from '../../types';
import { DOCUMENT_CATEGORIES } from '../../types';
import { Plus, Trash2, Edit2, Loader2, Save, X, Table2, AlignLeft } from 'lucide-react';

// ── Document-type form ─────────────────────────────────────────────────
interface TypeFormData {
  typeKey: string;
  displayName: string;
  pluginClass: string;
  category: DocumentCategory;
}
const EMPTY_TYPE_FORM: TypeFormData = { typeKey: '', displayName: '', pluginClass: 'Generic', category: 'General' };

// ── Field-mapping form ─────────────────────────────────────────────────
// "dataSource" drives allowMultiple: header = false, table = true.
// regexPattern is kept in the backend for Tesseract but hidden from the Claude-first UI.
// For header fields: displayOrder = displayRow * 10 + displayCol (e.g. row 1 col 2 → 12).
// For table fields:  displayOrder = displayCol (plain column order, 1-based).
interface FieldFormData {
  fieldName: string;
  displayLabel: string;
  dataSource: 'header' | 'table';
  searchHint: string;        // maps to keywordAnchor — tells Claude where to look
  isRequired: boolean;
  isManualEntry: boolean;    // if true, value is not extracted by OCR — user enters it manually
  isCheckbox: boolean;       // if true, renders as a checkbox toggle in the table view
  erpMappingKey: string;
  erpResponseField: string;  // which key in the ERP response to show on pass (e.g. "vendorId", "RefNbr")
  dependentFieldKey: string; // another field whose value must match for ERP cross-validation
  confidenceThreshold: number;
  displayRow: number;        // header fields: grid row (1-based); ignored for table fields
  displayCol: number;        // header fields: grid col (1-based); table fields: column order
}
const EMPTY_FORM: FieldFormData = {
  fieldName: '',
  displayLabel: '',
  dataSource: 'header',
  searchHint: '',
  isRequired: false,
  isManualEntry: false,
  isCheckbox: false,
  erpMappingKey: '',
  erpResponseField: '',
  dependentFieldKey: '',
  confidenceThreshold: 0.75,
  displayRow: 1,
  displayCol: 1,
};

// Map form data → API payload
function toApiData(form: FieldFormData) {
  const displayOrder = form.dataSource === 'header'
    ? form.displayRow * 10 + form.displayCol   // e.g. row 1 col 2 → 12
    : form.displayCol;                          // table fields: plain column order
  return {
    fieldName:           form.fieldName,
    displayLabel:        form.displayLabel,
    regexPattern:        '',          // not used with Claude
    keywordAnchor:       form.searchHint,
    isRequired:          form.isRequired,
    isManualEntry:       form.isManualEntry,
    isCheckbox:          form.isCheckbox,
    allowMultiple:       form.dataSource === 'table',
    erpMappingKey:       form.erpMappingKey || null,
    erpResponseField:    form.erpResponseField || null,
    dependentFieldKey:   form.dependentFieldKey || null,
    confidenceThreshold: form.confidenceThreshold,
    displayOrder,
  };
}

// Decode a stored displayOrder back into { row, col }.
// Header encoding: row * 10 + col (e.g. 12 → row 1, col 2).
// Legacy sequential values (< 10) are treated as row 1 with that col.
function decodeDisplayOrder(displayOrder: number, isHeader: boolean): { row: number; col: number } {
  if (!isHeader) return { row: 1, col: displayOrder };
  if (displayOrder >= 11) return { row: Math.floor(displayOrder / 10), col: displayOrder % 10 };
  return { row: 1, col: Math.max(displayOrder, 1) };  // legacy
}


export default function FieldMappingConfigPage() {
  const queryClient = useQueryClient();

  // Document-type state
  const [showTypeForm, setShowTypeForm] = useState(false);
  const [typeForm, setTypeForm] = useState<TypeFormData>(EMPTY_TYPE_FORM);
  const [showDeleteTypeModal, setShowDeleteTypeModal] = useState(false);
  const [editingDocType, setEditingDocType] = useState(false);
  const [docTypeEditForm, setDocTypeEditForm] = useState<{ displayName: string; category: DocumentCategory }>({ displayName: '', category: 'General' });

  // Field-mapping state
  const [selectedTypeId, setSelectedTypeId] = useState('');
  const [showForm, setShowForm] = useState(false);
  const [editingId, setEditingId] = useState<string | null>(null);
  const [form, setForm] = useState<FieldFormData>(EMPTY_FORM);

  // ── Queries ────────────────────────────────────────────────────────────
  const { data: erpEntities } = useQuery<ErpEntity[]>({
    queryKey: ['erp-entities'],
    queryFn: () => erpApi.getErpEntities().then(r => r.data),
    staleTime: Infinity,
  });

  const { data: docTypes } = useQuery<DocumentType[]>({
    queryKey: ['document-types'],
    queryFn: () => configApi.getDocumentTypes().then(r => r.data),
  });

  const { data: fields, isLoading } = useQuery<FieldMappingConfig[]>({
    queryKey: ['field-mappings', selectedTypeId],
    queryFn: () => configApi.getFieldMappings(selectedTypeId).then(r => r.data),
    enabled: !!selectedTypeId,
  });

  // ── Document-type mutation ─────────────────────────────────────────────
  const createDocType = useMutation({
    mutationFn: (data: TypeFormData) => configApi.registerDocumentType(data),
    onSuccess: (res) => {
      queryClient.invalidateQueries({ queryKey: ['document-types'] });
      setShowTypeForm(false);
      setTypeForm(EMPTY_TYPE_FORM);
      setSelectedTypeId(res.data.id);
    },
  });

  // ── Field mutations ────────────────────────────────────────────────────
  const createField = useMutation({
    mutationFn: (data: FieldFormData) => configApi.createFieldMapping(selectedTypeId, toApiData(data)),
    onSuccess: () => { queryClient.invalidateQueries({ queryKey: ['field-mappings', selectedTypeId] }); closeForm(); },
  });

  const updateField = useMutation({
    mutationFn: ({ id, data }: { id: string; data: FieldFormData }) =>
      configApi.updateFieldMapping(selectedTypeId, id, toApiData(data)),
    onSuccess: () => { queryClient.invalidateQueries({ queryKey: ['field-mappings', selectedTypeId] }); closeForm(); },
  });

  const deleteField = useMutation({
    mutationFn: (fieldId: string) => configApi.deleteFieldMapping(selectedTypeId, fieldId),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['field-mappings', selectedTypeId] }),
    onError: () => alert('Failed to delete field. Please try again.'),
  });

  const deleteDocType = useMutation({
    mutationFn: () => configApi.deleteDocumentType(selectedTypeId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['document-types'] });
      setSelectedTypeId('');
      setShowDeleteTypeModal(false);
    },
    onError: () => alert('Failed to delete document type. It may have associated documents.'),
  });

  const updateDocType = useMutation({
    mutationFn: () => configApi.updateDocumentType(selectedTypeId, docTypeEditForm),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['document-types'] });
      setEditingDocType(false);
    },
  });

  // ── Field form helpers ─────────────────────────────────────────────────
  const openCreate = () => { setForm(EMPTY_FORM); setEditingId(null); setShowForm(true); };
  const openEdit = (f: FieldMappingConfig) => {
    const isHeader = !f.allowMultiple;
    const { row, col } = decodeDisplayOrder(f.displayOrder, isHeader);
    setForm({
      fieldName:           f.fieldName,
      displayLabel:        f.displayLabel ?? '',
      dataSource:          f.allowMultiple ? 'table' : 'header',
      searchHint:          f.keywordAnchor ?? '',
      isRequired:          f.isRequired,
      isManualEntry:       f.isManualEntry ?? false,
      isCheckbox:          f.isCheckbox ?? false,
      erpMappingKey:       f.erpMappingKey ?? '',
      erpResponseField:    f.erpResponseField ?? '',
      dependentFieldKey:   f.dependentFieldKey ?? '',
      confidenceThreshold: f.confidenceThreshold,
      displayRow:          row,
      displayCol:          col,
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
      <div className="flex items-center justify-between">
        <h1 className="text-2xl font-bold text-foreground">Field Mapping Config</h1>
        <button onClick={() => setShowTypeForm(true)} className="btn-primary flex items-center gap-2 text-sm">
          <Plus className="h-4 w-4" /> New Document Type
        </button>
      </div>

      {/* Type selector + add-field button */}
      <div className="flex flex-col sm:flex-row gap-3 items-start sm:items-center">
        <select
          className="input w-auto"
          value={selectedTypeId}
          onChange={e => setSelectedTypeId(e.target.value)}
        >
          <option value="">— Select document type —</option>
          {docTypes?.map(dt => (
            <option key={dt.id} value={dt.id}>
              {dt.displayName}{dt.category !== 'General' ? ` [${dt.category}]` : ''}
            </option>
          ))}
        </select>
        {selectedTypeId && (() => {
          const dt = docTypes?.find(d => d.id === selectedTypeId);
          return dt?.category !== 'General' ? (
            <span className="text-xs px-2 py-1 rounded-full bg-emerald-100 text-emerald-700 font-medium">
              {DOCUMENT_CATEGORIES.find(c => c.value === dt?.category)?.label ?? dt?.category}
            </span>
          ) : null;
        })()}
        {selectedTypeId && (
          <button onClick={openCreate} className="btn-primary flex items-center gap-2 text-sm">
            <Plus className="h-4 w-4" /> Add Field
          </button>
        )}
        {selectedTypeId && (
          <button
            onClick={() => setShowDeleteTypeModal(true)}
            className="flex items-center gap-1.5 text-sm text-destructive border border-red-200 hover:bg-red-50 rounded-md px-3 py-2 transition-colors"
          >
            <Trash2 className="h-4 w-4" /> Delete Type
          </button>
        )}
        {!docTypes?.length && (
          <p className="text-sm text-muted-foreground">No document types yet — click "New Document Type" to create one.</p>
        )}
      </div>

      {/* Document type info / edit card */}
      {selectedTypeId && (() => {
        const dt = docTypes?.find(d => d.id === selectedTypeId);
        if (!dt) return null;
        const isStatement = dt.category === 'VendorStatement';

        if (editingDocType) {
          return (
            <div className="card px-4 py-4 space-y-3">
              <p className="text-xs font-semibold text-muted-foreground uppercase tracking-wide">Edit Document Type</p>
              <div className="flex flex-wrap gap-4 items-end">
                <div className="flex-1 min-w-48">
                  <label className="block text-xs text-muted-foreground mb-1">Display Name</label>
                  <input
                    className="input"
                    value={docTypeEditForm.displayName}
                    onChange={e => setDocTypeEditForm(f => ({ ...f, displayName: e.target.value }))}
                  />
                </div>
                <div>
                  <label className="block text-xs text-muted-foreground mb-1">Document Category</label>
                  <select
                    className="input"
                    value={docTypeEditForm.category}
                    onChange={e => setDocTypeEditForm(f => ({ ...f, category: e.target.value as DocumentCategory }))}
                  >
                    {DOCUMENT_CATEGORIES.map(c => (
                      <option key={c.value} value={c.value}>{c.label}</option>
                    ))}
                  </select>
                </div>
                <div className="flex gap-2">
                  <button
                    className="btn-primary flex items-center gap-1.5 text-sm"
                    onClick={() => updateDocType.mutate()}
                    disabled={updateDocType.isPending || !docTypeEditForm.displayName.trim()}
                  >
                    {updateDocType.isPending ? <Loader2 className="h-4 w-4 animate-spin" /> : <Save className="h-4 w-4" />}
                    Save
                  </button>
                  <button className="btn-secondary text-sm" onClick={() => setEditingDocType(false)}>
                    Cancel
                  </button>
                </div>
              </div>
              {updateDocType.isError && (
                <p className="text-xs text-destructive">Failed to save changes.</p>
              )}
            </div>
          );
        }

        return (
          <div className={`rounded-lg border px-4 py-3 flex flex-wrap items-center gap-x-6 gap-y-2 text-sm ${
            isStatement ? 'bg-emerald-50 border-emerald-200' : 'bg-muted/40 border-border'
          }`}>
            <div className="flex items-center gap-2">
              <span className="text-muted-foreground">Type Key</span>
              <span className="font-mono font-medium text-foreground">{dt.typeKey}</span>
            </div>
            <div className="flex items-center gap-2">
              <span className="text-muted-foreground">Category</span>
              <span className={`px-2 py-0.5 rounded-full text-xs font-semibold ${
                isStatement ? 'bg-emerald-100 text-emerald-800' : 'bg-muted text-muted-foreground'
              }`}>
                {DOCUMENT_CATEGORIES.find(c => c.value === dt.category)?.label ?? dt.category}
              </span>
            </div>
            {isStatement && (
              <span className="text-xs text-emerald-700">
                Vendor statement validators are <span className="font-semibold">enabled</span>.
              </span>
            )}
            <button
              className="ml-auto flex items-center gap-1.5 text-xs text-muted-foreground hover:text-foreground"
              onClick={() => {
                setDocTypeEditForm({ displayName: dt.displayName, category: dt.category as DocumentCategory });
                setEditingDocType(true);
              }}
            >
              <Edit2 className="h-3.5 w-3.5" /> Edit
            </button>
          </div>
        );
      })()}

      {/* Field tables — split into Header and Table sections */}
      {selectedTypeId && (() => {
        const headerFields = fields?.filter(f => !f.allowMultiple).sort((a, b) => a.displayOrder - b.displayOrder) ?? [];
        const tableFields  = fields?.filter(f =>  f.allowMultiple).sort((a, b) => a.displayOrder - b.displayOrder) ?? [];

        const FieldTable = ({ rows, emptyMsg, isHeader: isHeaderSection }: { rows: FieldMappingConfig[]; emptyMsg: string; isHeader: boolean }) => (
          <table className="w-full text-sm">
            <thead className="bg-muted/50 border-b border-border">
              <tr>
                <th className="px-4 py-3 text-left font-medium text-muted-foreground w-16">{isHeaderSection ? 'R/C' : 'Order'}</th>
                <th className="px-4 py-3 text-left font-medium text-muted-foreground">Field Name</th>
                <th className="px-4 py-3 text-left font-medium text-muted-foreground">ERP Key</th>
                <th className="px-4 py-3 text-left font-medium text-muted-foreground">Depends On</th>
                <th className="px-4 py-3 text-left font-medium text-muted-foreground">Success Label</th>
                <th className="px-4 py-3 text-left font-medium text-muted-foreground">Required</th>
                <th className="px-4 py-3 text-left font-medium text-muted-foreground">Source</th>
                <th className="px-4 py-3 text-left font-medium text-muted-foreground">Threshold</th>
                <th className="px-4 py-3 text-right font-medium text-muted-foreground">Actions</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-border">
              {isLoading && (
                <tr><td colSpan={9} className="px-4 py-8 text-center text-muted-foreground">Loading...</td></tr>
              )}
              {rows.map(f => {
                const { row, col } = decodeDisplayOrder(f.displayOrder, isHeaderSection);
                return (
                <tr key={f.id} className="hover:bg-muted/30">
                  <td className="px-4 py-3 text-muted-foreground font-mono text-xs">
                    {isHeaderSection
                      ? <span title={`Row ${row}, Col ${col}`}>R{row}/C{col}</span>
                      : <span>{f.displayOrder}</span>}
                  </td>
                  <td className="px-4 py-3">
                    <p className="font-medium text-foreground">{f.fieldName}</p>
                    {f.displayLabel && <p className="text-xs text-muted-foreground">{f.displayLabel}</p>}
                    {f.keywordAnchor && <p className="text-xs text-muted-foreground italic">Hint: {f.keywordAnchor}</p>}
                  </td>
                  <td className="px-4 py-3">
                    {f.erpMappingKey
                      ? <span className="text-xs px-2 py-0.5 rounded-full bg-muted text-muted-foreground font-medium">{f.erpMappingKey}</span>
                      : <span className="text-muted-foreground">—</span>}
                  </td>
                  <td className="px-4 py-3">
                    {f.dependentFieldKey
                      ? <span className="text-xs px-2 py-0.5 rounded-full bg-orange-100 text-orange-700 font-medium font-mono">{f.dependentFieldKey}</span>
                      : <span className="text-muted-foreground">—</span>}
                  </td>
                  <td className="px-4 py-3">
                    {f.erpResponseField
                      ? <span className="text-xs font-mono px-2 py-0.5 rounded bg-blue-50 text-blue-700">{f.erpResponseField}</span>
                      : <span className="text-muted-foreground">—</span>}
                  </td>
                  <td className="px-4 py-3">
                    {f.isRequired
                      ? <span className="text-xs px-2 py-0.5 rounded-full bg-amber-100 text-amber-700 font-medium">Required</span>
                      : <span className="text-muted-foreground text-xs">Optional</span>}
                  </td>
                  <td className="px-4 py-3">
                    {f.isCheckbox
                      ? <span className="text-xs px-2 py-0.5 rounded-full bg-violet-100 text-violet-700 font-medium">Checkbox</span>
                      : f.isManualEntry
                        ? <span className="text-xs px-2 py-0.5 rounded-full bg-violet-100 text-violet-700 font-medium">Manual</span>
                        : <span className="text-xs px-2 py-0.5 rounded-full bg-sky-100 text-sky-700 font-medium">OCR</span>}
                  </td>
                  <td className="px-4 py-3 text-foreground">{Math.round(f.confidenceThreshold * 100)}%</td>
                  <td className="px-4 py-3 text-right">
                    <div className="flex items-center justify-end gap-2">
                      <button onClick={() => openEdit(f)} className="text-muted-foreground hover:text-foreground">
                        <Edit2 className="h-4 w-4" />
                      </button>
                      <button
                        onClick={() => deleteField.mutate(f.id)}
                        disabled={deleteField.isPending}
                        className="text-muted-foreground hover:text-destructive"
                      >
                        <Trash2 className="h-4 w-4" />
                      </button>
                    </div>
                  </td>
                </tr>
                );
              })}
              {!isLoading && rows.length === 0 && (
                <tr><td colSpan={9} className="px-4 py-6 text-center text-muted-foreground text-xs">{emptyMsg}</td></tr>
              )}
            </tbody>
          </table>
        );

        return (
          <div className="space-y-4">
            {/* Header fields */}
            <div className="card overflow-x-auto">
              <div className="px-4 py-3 border-b border-border bg-blue-50 flex items-center gap-2">
                <AlignLeft className="h-4 w-4 text-blue-600" />
                <span className="font-semibold text-blue-700 text-sm">Header Fields</span>
                <span className="text-xs text-blue-500">— single value per document</span>
              </div>
              <FieldTable rows={headerFields} emptyMsg="No header fields yet." isHeader={true} />
            </div>

            {/* Table fields */}
            <div className="card overflow-x-auto">
              <div className="px-4 py-3 border-b border-border bg-purple-50 flex items-center gap-2">
                <Table2 className="h-4 w-4 text-purple-600" />
                <span className="font-semibold text-purple-700 text-sm">Table Fields</span>
                <span className="text-xs text-purple-500">— multiple rows per document</span>
              </div>
              <FieldTable rows={tableFields} emptyMsg="No table fields yet." isHeader={false} />
            </div>
          </div>
        );
      })()}

      {/* ── New Document Type modal ──────────────────────────────────────── */}
      {showTypeForm && (
        <div className="fixed inset-0 z-50 flex items-center justify-center p-4 bg-black/50">
          <div className="card p-6 w-full max-w-md space-y-4">
            <div className="flex items-center justify-between">
              <h3 className="font-semibold text-foreground">New Document Type</h3>
              <button onClick={() => { setShowTypeForm(false); setTypeForm(EMPTY_TYPE_FORM); }}>
                <X className="h-5 w-5 text-muted-foreground" />
              </button>
            </div>

            <div className="space-y-3">
              <Field label="Display Name *">
                <input
                  className="input"
                  placeholder="e.g. Purchase Invoice"
                  value={typeForm.displayName}
                  onChange={e => setTypeForm(f => ({ ...f, displayName: e.target.value }))}
                />
              </Field>
              <Field label="Type Key *">
                <input
                  className="input font-mono text-sm"
                  placeholder="e.g. purchase_invoice"
                  value={typeForm.typeKey}
                  onChange={e => setTypeForm(f => ({ ...f, typeKey: e.target.value }))}
                />
                <p className="text-xs text-muted-foreground mt-1">Unique identifier. Use lowercase with underscores.</p>
              </Field>
              <Field label="Plugin Class">
                <input
                  className="input font-mono text-sm"
                  placeholder="Generic"
                  value={typeForm.pluginClass}
                  onChange={e => setTypeForm(f => ({ ...f, pluginClass: e.target.value }))}
                />
                <p className="text-xs text-muted-foreground mt-1">Leave as "Generic" unless you have a custom processing plugin.</p>
              </Field>
              <Field label="Document Category">
                <select
                  className="input"
                  value={typeForm.category}
                  onChange={e => setTypeForm(f => ({ ...f, category: e.target.value as DocumentCategory }))}
                >
                  {DOCUMENT_CATEGORIES.map(c => (
                    <option key={c.value} value={c.value}>{c.label}</option>
                  ))}
                </select>
                <p className="text-xs text-muted-foreground mt-1">
                  Controls which specialised validators and processes run for this document type.
                  Use <span className="font-medium">Vendor Statement</span> to enable vendor statement reconciliation.
                </p>
              </Field>
            </div>

            {createDocType.isError && (
              <p className="text-sm text-destructive">Failed to create document type. The type key may already be in use.</p>
            )}

            <div className="flex justify-end gap-3 pt-2">
              <button className="btn-secondary" onClick={() => { setShowTypeForm(false); setTypeForm(EMPTY_TYPE_FORM); }}>Cancel</button>
              <button
                className="btn-primary flex items-center gap-2"
                onClick={() => createDocType.mutate(typeForm)}
                disabled={createDocType.isPending || !typeForm.displayName.trim() || !typeForm.typeKey.trim()}
              >
                {createDocType.isPending ? <Loader2 className="h-4 w-4 animate-spin" /> : <Save className="h-4 w-4" />}
                Create
              </button>
            </div>
          </div>
        </div>
      )}

      {/* ── Delete Document Type confirmation modal ──────────────────────── */}
      {showDeleteTypeModal && (
        <div className="fixed inset-0 z-50 flex items-center justify-center p-4 bg-black/50">
          <div className="card p-6 w-full max-w-md space-y-4">
            <div className="flex items-center justify-between">
              <h3 className="font-semibold text-foreground text-destructive">Delete Document Type</h3>
              <button onClick={() => setShowDeleteTypeModal(false)}><X className="h-5 w-5 text-muted-foreground" /></button>
            </div>
            <p className="text-sm text-foreground">
              Are you sure you want to delete <span className="font-medium">{docTypes?.find(dt => dt.id === selectedTypeId)?.displayName}</span>?
              This will also delete all its field mappings and cannot be undone.
            </p>
            {deleteDocType.isError && (
              <p className="text-sm text-destructive">Failed to delete. The type may have associated documents.</p>
            )}
            <div className="flex justify-end gap-3 pt-2">
              <button className="btn-secondary" onClick={() => setShowDeleteTypeModal(false)}>Cancel</button>
              <button
                className="btn-primary bg-destructive hover:bg-destructive/90 flex items-center gap-2"
                onClick={() => deleteDocType.mutate()}
                disabled={deleteDocType.isPending}
              >
                {deleteDocType.isPending ? <Loader2 className="h-4 w-4 animate-spin" /> : <Trash2 className="h-4 w-4" />}
                Delete
              </button>
            </div>
          </div>
        </div>
      )}

      {/* ── Add / Edit Field modal ───────────────────────────────────────── */}
      {showForm && (
        <div className="fixed inset-0 z-50 flex items-center justify-center p-4 bg-black/50">
          <div className="card p-6 w-full max-w-lg space-y-4 max-h-[90vh] overflow-y-auto">
            <div className="flex items-center justify-between">
              <h3 className="font-semibold text-foreground">{editingId ? 'Edit Field' : 'Add Field'}</h3>
              <button onClick={closeForm}><X className="h-5 w-5 text-muted-foreground" /></button>
            </div>

            <div className="space-y-4">

              {/* Field Name */}
              <Field label="Field Name *">
                <input
                  className="input font-mono text-sm"
                  placeholder="e.g. invoiceNumber"
                  value={form.fieldName}
                  onChange={e => setForm(f => ({ ...f, fieldName: e.target.value }))}
                />
                <p className="text-xs text-muted-foreground mt-1">
                  Exact name Claude will use. Use camelCase (e.g. vendorName, invoiceDate).
                </p>
              </Field>

              {/* Display Label */}
              <Field label="Display Label">
                <input
                  className="input"
                  placeholder="e.g. Invoice Number"
                  value={form.displayLabel}
                  onChange={e => setForm(f => ({ ...f, displayLabel: e.target.value }))}
                />
                <p className="text-xs text-muted-foreground mt-1">Human-readable name shown in the UI.</p>
              </Field>

              {/* Data Source */}
              <Field label="Data Source">
                <div className="grid grid-cols-2 gap-3 mt-1">
                  <label className={`flex items-center gap-3 border rounded-lg p-3 cursor-pointer transition-colors ${
                    form.dataSource === 'header'
                      ? 'border-primary bg-primary/5'
                      : 'border-border hover:bg-muted/50'
                  }`}>
                    <input
                      type="radio"
                      name="dataSource"
                      value="header"
                      checked={form.dataSource === 'header'}
                      onChange={() => setForm(f => ({ ...f, dataSource: 'header' }))}
                      className="accent-primary"
                    />
                    <div>
                      <p className="text-sm font-medium text-foreground flex items-center gap-1.5">
                        <AlignLeft className="h-4 w-4" /> Header
                      </p>
                      <p className="text-xs text-muted-foreground">Single value — e.g. vendor name, date, total</p>
                    </div>
                  </label>
                  <label className={`flex items-center gap-3 border rounded-lg p-3 cursor-pointer transition-colors ${
                    form.dataSource === 'table'
                      ? 'border-primary bg-primary/5'
                      : 'border-border hover:bg-muted/50'
                  }`}>
                    <input
                      type="radio"
                      name="dataSource"
                      value="table"
                      checked={form.dataSource === 'table'}
                      onChange={() => setForm(f => ({ ...f, dataSource: 'table' }))}
                      className="accent-primary"
                    />
                    <div>
                      <p className="text-sm font-medium text-foreground flex items-center gap-1.5">
                        <Table2 className="h-4 w-4" /> Table
                      </p>
                      <p className="text-xs text-muted-foreground">Multiple rows — e.g. line items, transactions</p>
                    </div>
                  </label>
                </div>
              </Field>

              {/* Search Hint */}
              <Field label="Search Hint">
                <input
                  className="input"
                  placeholder={form.dataSource === 'table'
                    ? 'e.g. column header "Description" or "Item"'
                    : 'e.g. label near the value "Invoice No." or "Date"'}
                  value={form.searchHint}
                  onChange={e => setForm(f => ({ ...f, searchHint: e.target.value }))}
                />
                <p className="text-xs text-muted-foreground mt-1">
                  Optional. Tells Claude where to find this field — e.g. a nearby label or column header.
                </p>
              </Field>

              {/* ERP Mapping — Entity + Field selectors */}
              <ErpMappingField
                value={form.erpMappingKey}
                onChange={v => setForm(f => ({ ...f, erpMappingKey: v }))}
                entities={erpEntities ?? []}
              />

              {/* Dependent Field (cross-validation) */}
              <Field label="Dependent Field (Cross-Validation)">
                <select
                  className="input text-sm"
                  value={form.dependentFieldKey}
                  onChange={e => setForm(f => ({ ...f, dependentFieldKey: e.target.value }))}
                >
                  <option value="">— None —</option>
                  {fields
                    ?.filter(f => f.fieldName !== form.fieldName)
                    .sort((a, b) => a.displayOrder - b.displayOrder)
                    .map(f => (
                      <option key={f.id} value={f.fieldName}>
                        {f.displayLabel ?? f.fieldName}
                      </option>
                    ))}
                </select>
                <p className="text-xs text-muted-foreground mt-1">
                  When set, ERP validation will also cross-check this field's value against the selected field.
                  Example: set <span className="font-mono">Invoice Number</span> to depend on <span className="font-mono">Vendor Name</span>
                  so the invoice is verified to belong to that vendor.
                  If the dependent field is an own-company name (e.g. Geohan), the cross-check is skipped automatically.
                </p>
              </Field>

              {/* Validation Success Label */}
              <Field label="Validation Success Label">
                <input
                  className="input font-mono text-sm"
                  placeholder="e.g. vendorId or RefNbr"
                  value={form.erpResponseField}
                  onChange={e => setForm(f => ({ ...f, erpResponseField: e.target.value }))}
                />
                <p className="text-xs text-muted-foreground mt-1">
                  JSON key from the ERP response to display when validation passes — e.g. <span className="font-mono">vendorId</span> shows "✓ vendorId: V00001", <span className="font-mono">RefNbr</span> shows "✓ RefNbr: GESB-001".
                </p>
              </Field>

              {/* Confidence Threshold */}
              <Field label={`Confidence Threshold: ${Math.round(form.confidenceThreshold * 100)}%`}>
                <input
                  type="range" min="0" max="100" step="5"
                  value={Math.round(form.confidenceThreshold * 100)}
                  onChange={e => setForm(f => ({ ...f, confidenceThreshold: Number(e.target.value) / 100 }))}
                  className="w-full mt-1"
                />
              </Field>

              {/* Display position */}
              {form.dataSource === 'header' ? (
                <div>
                  <label className="block text-sm font-medium text-foreground mb-1">
                    Display Position
                  </label>
                  <div className="grid grid-cols-2 gap-3">
                    <div>
                      <label className="block text-xs text-muted-foreground mb-1">Row</label>
                      <input
                        type="number" min="1" max="9"
                        className="input"
                        value={form.displayRow}
                        onChange={e => setForm(f => ({ ...f, displayRow: Math.max(1, Number(e.target.value)) }))}
                      />
                    </div>
                    <div>
                      <label className="block text-xs text-muted-foreground mb-1">Column</label>
                      <input
                        type="number" min="1" max="9"
                        className="input"
                        value={form.displayCol}
                        onChange={e => setForm(f => ({ ...f, displayCol: Math.max(1, Number(e.target.value)) }))}
                      />
                    </div>
                  </div>
                  <p className="text-xs text-muted-foreground mt-1">
                    Grid position in the document detail view. Row 1 = top, Col 1 = left.
                  </p>
                </div>
              ) : (
                <Field label="Column Order">
                  <input
                    type="number" min="1"
                    className="input"
                    value={form.displayCol}
                    onChange={e => setForm(f => ({ ...f, displayCol: Math.max(1, Number(e.target.value)) }))}
                  />
                  <p className="text-xs text-muted-foreground mt-1">
                    Left-to-right order of this column in the table view (1 = first column).
                  </p>
                </Field>
              )}

              {/* Required checkbox */}
              <label className="flex items-center gap-2 cursor-pointer">
                <input
                  type="checkbox"
                  checked={form.isRequired}
                  onChange={e => setForm(f => ({ ...f, isRequired: e.target.checked }))}
                  className="rounded border-border"
                />
                <span className="text-sm text-foreground">Required field</span>
                <span className="text-xs text-muted-foreground">(validation will flag if missing)</span>
              </label>

              {/* Manual entry toggle */}
              <label className={`flex items-start gap-3 border rounded-lg p-3 cursor-pointer transition-colors ${
                form.isManualEntry ? 'border-violet-400 bg-violet-50' : 'border-border hover:bg-muted/30'
              }`}>
                <input
                  type="checkbox"
                  checked={form.isManualEntry}
                  onChange={e => setForm(f => ({ ...f, isManualEntry: e.target.checked }))}
                  className="mt-0.5 rounded border-border accent-violet-600"
                />
                <div>
                  <p className="text-sm font-medium text-foreground">Manual entry field</p>
                  <p className="text-xs text-muted-foreground mt-0.5">
                    Value is not extracted from the document. The user must enter it manually during review.
                    OCR and Claude will skip this field entirely (unless "Display as checkbox" is also enabled).
                  </p>
                </div>
              </label>

              {/* Checkbox display toggle — shown for any table field */}
              {form.dataSource === 'table' && (
                <label className={`flex items-start gap-3 border rounded-lg p-3 cursor-pointer transition-colors ${
                  form.isCheckbox ? 'border-violet-400 bg-violet-50' : 'border-border hover:bg-muted/30'
                }`}>
                  <input
                    type="checkbox"
                    checked={form.isCheckbox}
                    onChange={e => setForm(f => ({ ...f, isCheckbox: e.target.checked }))}
                    className="mt-0.5 rounded border-border accent-violet-600"
                  />
                  <div>
                    <p className="text-sm font-medium text-foreground">Display as checkbox</p>
                    <p className="text-xs text-muted-foreground mt-0.5">
                      Renders a checkbox toggle per table row. Claude will automatically detect payment/credit
                      indicators (PAID, CR, zero balance, etc.) and pre-tick settled rows.
                      Reviewers can still manually override any row. Settled rows are excluded from balance validation.
                    </p>
                  </div>
                </label>
              )}

            </div>

            <div className="flex justify-end gap-3 pt-2 border-t border-border">
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
      <label className="block text-sm font-medium text-foreground mb-1">{label}</label>
      {children}
    </div>
  );
}

// ── ERP Mapping Field ─────────────────────────────────────────────────────────
// Stores value as "Entity:FieldName" (e.g. "Vendor:VendorName").
// Legacy single-word keys (e.g. "VendorID") are shown in a read-only banner
// so existing configs aren't silently broken.

function ErpMappingField({
  value, onChange, entities,
}: {
  value: string;
  onChange: (v: string) => void;
  entities: ErpEntity[];
}) {
  const isLegacy = !!value && !value.includes(':');
  const colonIdx = value.indexOf(':');
  const selectedEntity = colonIdx > -1 ? value.slice(0, colonIdx) : '';
  const selectedField  = colonIdx > -1 ? value.slice(colonIdx + 1) : '';

  const entityObj = entities.find(e => e.entityName === selectedEntity);

  const setEntity = (entityName: string) => {
    onChange(entityName ? `${entityName}:` : '');
  };
  const setField = (fieldName: string) => {
    onChange(selectedEntity ? `${selectedEntity}:${fieldName}` : '');
  };

  return (
    <div className="space-y-2">
      <label className="block text-sm font-medium text-foreground">ERP Validation</label>

      {isLegacy && (
        <div className="rounded-md border border-amber-200 bg-amber-50 px-3 py-2 text-xs text-amber-700 flex items-center justify-between gap-2">
          <span>Legacy key: <span className="font-mono font-medium">{value}</span> — still works but consider re-selecting below.</span>
          <button
            type="button"
            className="text-amber-600 hover:text-amber-900 underline text-xs"
            onClick={() => onChange('')}
          >
            Clear
          </button>
        </div>
      )}

      <div className="grid grid-cols-2 gap-2">
        <div>
          <label className="block text-xs text-muted-foreground mb-1">Acumatica Entity</label>
          <select
            className="input text-sm"
            value={selectedEntity}
            onChange={e => setEntity(e.target.value)}
          >
            <option value="">— None (no ERP check) —</option>
            {entities.map(e => (
              <option key={e.entityName} value={e.entityName}>{e.displayName}</option>
            ))}
          </select>
        </div>
        <div>
          <label className="block text-xs text-muted-foreground mb-1">Match Field</label>
          {entityObj ? (
            <select
              className="input text-sm"
              value={selectedField}
              onChange={e => setField(e.target.value)}
            >
              <option value="">— Select field —</option>
              {entityObj.fields.map(f => (
                <option key={f} value={f}>{f}</option>
              ))}
            </select>
          ) : (
            <input
              className="input text-sm bg-muted/50"
              disabled
              placeholder="Select entity first"
            />
          )}
        </div>
      </div>

      {selectedEntity && selectedField && (
        <p className="text-xs text-muted-foreground">
          Will validate: <span className="font-mono text-foreground">{selectedEntity}:{selectedField}</span> —
          checks that the extracted value exists in Acumatica's <span className="font-mono">{selectedEntity}</span> entity
          where <span className="font-mono">{selectedField}</span> matches.
        </p>
      )}
      {!selectedEntity && !isLegacy && (
        <p className="text-xs text-muted-foreground">No ERP validation — leave as-is for non-ERP fields.</p>
      )}
    </div>
  );
}
