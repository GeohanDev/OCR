import { useState } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { configApi, erpApi } from '../../api/client';
import type { DocumentType, FieldMappingConfig, ErpEntity } from '../../types';
import { Plus, Trash2, Edit2, Loader2, Save, X, Table2, AlignLeft } from 'lucide-react';

// ── Document-type form ─────────────────────────────────────────────────
interface TypeFormData {
  typeKey: string;
  displayName: string;
  pluginClass: string;
}
const EMPTY_TYPE_FORM: TypeFormData = { typeKey: '', displayName: '', pluginClass: 'Generic' };

// ── Field-mapping form ─────────────────────────────────────────────────
// "dataSource" drives allowMultiple: header = false, table = true.
// regexPattern is kept in the backend for Tesseract but hidden from the Claude-first UI.
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
  displayOrder: number;
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
  displayOrder: 0,
};

// Map form data → API payload
function toApiData(form: FieldFormData) {
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
    displayOrder:        form.displayOrder,
  };
}


export default function FieldMappingConfigPage() {
  const queryClient = useQueryClient();

  // Document-type state
  const [showTypeForm, setShowTypeForm] = useState(false);
  const [typeForm, setTypeForm] = useState<TypeFormData>(EMPTY_TYPE_FORM);
  const [showDeleteTypeModal, setShowDeleteTypeModal] = useState(false);

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

  // ── Field form helpers ─────────────────────────────────────────────────
  const openCreate = () => { setForm(EMPTY_FORM); setEditingId(null); setShowForm(true); };
  const openEdit = (f: FieldMappingConfig) => {
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
      displayOrder:        f.displayOrder,
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
          {docTypes?.map(dt => <option key={dt.id} value={dt.id}>{dt.displayName}</option>)}
        </select>
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

      {/* Field tables — split into Header and Table sections */}
      {selectedTypeId && (() => {
        const headerFields = fields?.filter(f => !f.allowMultiple).sort((a, b) => a.displayOrder - b.displayOrder) ?? [];
        const tableFields  = fields?.filter(f =>  f.allowMultiple).sort((a, b) => a.displayOrder - b.displayOrder) ?? [];

        const FieldTable = ({ rows, emptyMsg }: { rows: FieldMappingConfig[]; emptyMsg: string }) => (
          <table className="w-full text-sm">
            <thead className="bg-muted/50 border-b border-border">
              <tr>
                <th className="px-4 py-3 text-left font-medium text-muted-foreground w-8">#</th>
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
              {rows.map(f => (
                <tr key={f.id} className="hover:bg-muted/30">
                  <td className="px-4 py-3 text-muted-foreground">{f.displayOrder}</td>
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
              ))}
              {!isLoading && rows.length === 0 && (
                <tr><td colSpan={9} className="px-4 py-6 text-center text-muted-foreground text-xs">{emptyMsg}</td></tr>
              )}
            </tbody>
          </table>
        );

        return (
          <div className="space-y-4">
            {/* Header fields */}
            <div className="card overflow-hidden">
              <div className="px-4 py-3 border-b border-border bg-blue-50 flex items-center gap-2">
                <AlignLeft className="h-4 w-4 text-blue-600" />
                <span className="font-semibold text-blue-700 text-sm">Header Fields</span>
                <span className="text-xs text-blue-500">— single value per document</span>
              </div>
              <FieldTable rows={headerFields} emptyMsg="No header fields yet." />
            </div>

            {/* Table fields */}
            <div className="card overflow-hidden">
              <div className="px-4 py-3 border-b border-border bg-purple-50 flex items-center gap-2">
                <Table2 className="h-4 w-4 text-purple-600" />
                <span className="font-semibold text-purple-700 text-sm">Table Fields</span>
                <span className="text-xs text-purple-500">— multiple rows per document</span>
              </div>
              <FieldTable rows={tableFields} emptyMsg="No table fields yet." />
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

              {/* Required + Threshold in a row */}
              <div className="grid grid-cols-2 gap-4">
                <Field label={`Confidence Threshold: ${Math.round(form.confidenceThreshold * 100)}%`}>
                  <input
                    type="range" min="0" max="100" step="5"
                    value={Math.round(form.confidenceThreshold * 100)}
                    onChange={e => setForm(f => ({ ...f, confidenceThreshold: Number(e.target.value) / 100 }))}
                    className="w-full mt-1"
                  />
                </Field>
                <Field label="Display Order">
                  <input
                    type="number"
                    className="input"
                    value={form.displayOrder}
                    onChange={e => setForm(f => ({ ...f, displayOrder: Number(e.target.value) }))}
                  />
                </Field>
              </div>

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
                  onChange={e => setForm(f => ({ ...f, isManualEntry: e.target.checked, isCheckbox: e.target.checked ? f.isCheckbox : false }))}
                  className="mt-0.5 rounded border-border accent-violet-600"
                />
                <div>
                  <p className="text-sm font-medium text-foreground">Manual entry field</p>
                  <p className="text-xs text-muted-foreground mt-0.5">
                    Value is not extracted from the document. The user must enter it manually during review.
                    OCR and Claude will skip this field entirely.
                  </p>
                </div>
              </label>

              {/* Checkbox display toggle — only shown for manual entry table fields */}
              {form.isManualEntry && form.dataSource === 'table' && (
                <label className={`flex items-start gap-3 border rounded-lg p-3 cursor-pointer transition-colors ml-4 ${
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
                      Renders a checkbox toggle per table row instead of a text input.
                      Use this for boolean flags like "Settled by payment/credit note".
                      Rows with this checked will show a "Settled" badge and be excluded from balance validation.
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
