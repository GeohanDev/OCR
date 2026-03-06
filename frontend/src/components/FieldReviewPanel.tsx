import { useState, useRef, useEffect, useCallback, useMemo } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { ocrApi, validationApi } from '../api/client';
// validationApi.run is intentionally unused here — Run Validation fires per-field mutations instead.
import type { ExtractedField, ValidationResult, FieldMappingConfig } from '../types';
import { CheckCircle, XCircle, AlertTriangle, ChevronDown, ChevronUp, Pencil, Trash2, RefreshCw, Loader2 } from 'lucide-react';

interface FieldReviewPanelProps {
  documentId: string;
  fields: ExtractedField[];
  rawText?: string;
  fieldConfigs?: FieldMappingConfig[];
  onFieldSelect?: (field: ExtractedField | null) => void;
  selectedFieldId?: string;
}

// ── Inline editable cell ──────────────────────────────────────────────────────

function InlineEditCell({
  field,
  onSave,
}: {
  field: ExtractedField;
  onSave: (id: string, val: string) => void;
}) {
  const displayVal = field.isManuallyCorreected && field.correctedValue
    ? field.correctedValue
    : field.normalizedValue ?? field.rawValue ?? '';

  const [editing, setEditing] = useState(false);
  const [draft, setDraft] = useState(displayVal);
  const inputRef = useRef<HTMLInputElement>(null);

  useEffect(() => { setDraft(displayVal); }, [displayVal]);
  useEffect(() => {
    if (editing) { inputRef.current?.focus(); inputRef.current?.select(); }
  }, [editing]);

  const save = useCallback(() => {
    setEditing(false);
    if (draft.trim() !== displayVal) onSave(field.id, draft.trim());
  }, [draft, displayVal, field.id, onSave]);

  const cancel = useCallback(() => { setEditing(false); setDraft(displayVal); }, [displayVal]);

  if (editing) {
    return (
      <input
        ref={inputRef}
        value={draft}
        onChange={e => setDraft(e.target.value)}
        onBlur={save}
        onKeyDown={e => {
          if (e.key === 'Enter') { e.preventDefault(); save(); }
          else if (e.key === 'Escape') { e.preventDefault(); cancel(); }
        }}
        className="border border-ring rounded px-1.5 py-0.5 text-sm w-full bg-background focus:outline-none focus:ring-1 focus:ring-ring"
      />
    );
  }

  return (
    <span
      onClick={() => { setDraft(displayVal); setEditing(true); }}
      title="Click to edit"
      className="cursor-pointer rounded px-1 -mx-1 transition-colors hover:bg-primary/10 text-sm break-words"
    >
      {field.isManuallyCorreected && (
        <Pencil className="inline h-2.5 w-2.5 text-primary mr-0.5 mb-0.5" />
      )}
      {displayVal || <span className="text-muted-foreground">—</span>}
    </span>
  );
}

// ── Confidence badge ──────────────────────────────────────────────────────────

function ConfBadge({ value }: { value: number }) {
  return (
    <span className={`text-xs px-1.5 py-0.5 rounded-full font-medium flex-shrink-0 ${
      value >= 0.9 ? 'bg-green-100 text-green-800'
      : value >= 0.7 ? 'bg-amber-100 text-amber-800'
      : 'bg-red-100 text-red-800'
    }`}>
      {Math.round(value * 100)}%
    </span>
  );
}

// ── Main panel ────────────────────────────────────────────────────────────────

export default function FieldReviewPanel({
  documentId,
  fields,
  rawText,
  fieldConfigs,
  onFieldSelect,
  selectedFieldId,
}: FieldReviewPanelProps) {
  const queryClient = useQueryClient();
  const [showSummary, setShowSummary] = useState(true);
  const [sessionError, setSessionError] = useState<string | null>(null);
  // Tracks which field IDs are currently being validated (for spinner/pending UI).
  const [pendingIds, setPendingIds] = useState<Set<string>>(new Set());
  // Maps each table field ID → the full list of field IDs in its row, so that
  // editing any cell triggers validation for all columns in that row.
  const fieldToRowRef = useRef<Map<string, string[]>>(new Map());

  const { data: validations } = useQuery<ValidationResult[]>({
    queryKey: ['validation', documentId],
    queryFn: () => validationApi.getResults(documentId).then(r => r.data),
    retry: false,
  });

  // Per-field validation: only re-validates the changed field and surgically
  // updates the cache — other fields' results are preserved.
  const validateField = useMutation({
    mutationFn: (fieldId: string) =>
      validationApi.validateField(documentId, fieldId).then(r => r.data as ValidationResult[]),
    onMutate: (fieldId) => {
      setSessionError(null);
      setPendingIds(prev => new Set([...prev, fieldId]));
    },
    onSettled: (_, __, fieldId) => {
      setPendingIds(prev => { const next = new Set(prev); next.delete(fieldId); return next; });
    },
    onSuccess: (newResults, fieldId) => {
      queryClient.setQueryData<ValidationResult[]>(['validation', documentId], old => {
        const base = old ?? [];
        return [...base.filter(v => v.extractedFieldId !== fieldId), ...newResults];
      });
    },
    onError: (error: unknown) => {
      const status = (error as { response?: { status?: number } })?.response?.status;
      if (status === 424) {
        const msg = (error as { response?: { data?: { message?: string } } })?.response?.data?.message;
        setSessionError(msg ?? 'Acumatica session expired — please sign out and sign in again.');
      }
    },
  });

  // All field IDs — used to fire individual validations in parallel when Run Validation is clicked.
  const allFieldIds = useMemo(() => fields.map(f => f.id), [fields]);
  const runAllValidations = useCallback(() => {
    setSessionError(null);
    allFieldIds.forEach(id => validateField.mutate(id));
  }, [allFieldIds, validateField]);
  const isValidating = allFieldIds.some(id => pendingIds.has(id));

  const correctField = useMutation({
    mutationFn: ({ fieldId, value }: { fieldId: string; value: string }) =>
      ocrApi.correctField(documentId, fieldId, value),
    onSuccess: (_, { fieldId }) => {
      queryClient.invalidateQueries({ queryKey: ['ocr-result', documentId] });
      const rowIds = fieldToRowRef.current.get(fieldId);
      if (rowIds && rowIds.length > 1) {
        // Table cell: validate every field in the same row.
        rowIds.forEach(id => validateField.mutate(id));
      } else {
        validateField.mutate(fieldId);
      }
    },
  });

  // Delete all field IDs belonging to a table row (one per column at that index).
  const deleteRow = useMutation({
    mutationFn: (fieldIds: string[]) =>
      Promise.all(fieldIds.map(id => ocrApi.deleteField(documentId, id))),
    onSuccess: () => {
      // Invalidate both so the server re-fetches reflect the deleted field's
      // validation results being removed (backend deletes them on field delete).
      queryClient.invalidateQueries({ queryKey: ['ocr-result', documentId] });
      queryClient.invalidateQueries({ queryKey: ['validation', documentId] });
    },
  });

  const save = useCallback((fieldId: string, value: string) => {
    correctField.mutate({ fieldId, value });
  }, [correctField]);

  const allValidations = validations ?? [];
  const passed   = allValidations.filter(v => v.status === 'Passed').length;
  const failed   = allValidations.filter(v => v.status === 'Failed').length;
  const warnings = allValidations.filter(v => v.status === 'Warning').length;
  const canApprove = failed === 0 && allValidations.length > 0;

  // Robust ERP reference value lookup — case-insensitive, handles string-encoded JSON.
  const getErpValue = (erpReference: unknown, key: string): string | undefined => {
    if (!key) return undefined;
    let ref = erpReference;
    if (typeof ref === 'string') { try { ref = JSON.parse(ref); } catch { return undefined; } }
    if (!ref || typeof ref !== 'object') return undefined;
    const obj = ref as Record<string, unknown>;
    if (key in obj) return obj[key] != null ? String(obj[key]) : undefined;
    const lower = key.toLowerCase();
    const entry = Object.entries(obj).find(([k]) => k.toLowerCase() === lower);
    return entry && entry[1] != null ? String(entry[1]) : undefined;
  };

  // ── Field config lookup ───────────────────────────────────────────────
  const fieldConfigMap = useMemo(() => {
    const map: Record<string, FieldMappingConfig> = {};
    for (const c of fieldConfigs ?? []) map[c.fieldName.toLowerCase()] = c;
    return map;
  }, [fieldConfigs]);

  const isTableField = useCallback((fieldName: string) => {
    const cfg = fieldConfigMap[fieldName.toLowerCase()];
    if (cfg) return cfg.allowMultiple;
    return (fields.filter(f => f.fieldName === fieldName).length) > 1;
  }, [fieldConfigMap, fields]);

  // Header fields: single-value, first occurrence of each name
  const headerFields = useMemo(() =>
    fields.filter((f, i, arr) =>
      !isTableField(f.fieldName) &&
      arr.findIndex(x => x.fieldName === f.fieldName) === i
    ),
  [fields, isTableField]);

  // Table fields: grouped by fieldName
  const tableColumnNames = useMemo(() =>
    [...new Set(fields.filter(f => isTableField(f.fieldName)).map(f => f.fieldName))],
  [fields, isTableField]);

  const tableFieldGroups = useMemo(() => {
    const groups: Record<string, ExtractedField[]> = {};
    for (const name of tableColumnNames)
      groups[name] = fields.filter(f => f.fieldName === name);
    return groups;
  }, [tableColumnNames, fields]);

  const tableRowCount = useMemo(() =>
    Math.max(...Object.values(tableFieldGroups).map(g => g.length), 0),
  [tableFieldGroups]);

  // Keep fieldToRowRef current so correctField.onSuccess can find sibling IDs.
  useEffect(() => {
    const map = new Map<string, string[]>();
    for (let i = 0; i < tableRowCount; i++) {
      const rowIds = tableColumnNames
        .map(n => tableFieldGroups[n]?.[i]?.id)
        .filter((id): id is string => !!id);
      for (const id of rowIds) map.set(id, rowIds);
    }
    fieldToRowRef.current = map;
  }, [tableColumnNames, tableFieldGroups, tableRowCount]);

  const getValidationStatus = (fieldId: string) => {
    const vs = allValidations.filter(v => v.extractedFieldId === fieldId);
    if (vs.some(v => v.status === 'Failed'))  return 'Failed';
    if (vs.some(v => v.status === 'Warning')) return 'Warning';
    if (vs.some(v => v.status === 'Passed'))  return 'Passed';
    return null;
  };

  return (
    <div className="flex flex-col h-full">

      {/* ── Session expired banner ────────────────────────────────────── */}
      {sessionError && (
        <div className="flex items-center gap-2 px-3 py-2 bg-amber-50 border-b border-amber-200 text-xs text-amber-800 flex-shrink-0">
          <AlertTriangle className="h-3.5 w-3.5 flex-shrink-0" />
          <span className="flex-1">{sessionError}</span>
          <button onClick={() => setSessionError(null)} className="text-amber-500 hover:text-amber-700">✕</button>
        </div>
      )}

      {/* ── Validation summary ────────────────────────────────────────── */}
      <div className="border-b border-border bg-background flex-shrink-0">
        <div className="flex items-center">
          <button
            className="flex-1 flex items-center justify-between px-4 py-3"
            onClick={() => setShowSummary(s => !s)}
          >
            <div className="flex items-center gap-2">
              {allValidations.length > 0
                ? canApprove
                  ? <CheckCircle className="h-4 w-4 text-green-500" />
                  : <XCircle className="h-4 w-4 text-red-500" />
                : <AlertTriangle className="h-4 w-4 text-muted-foreground" />}
              <span className="text-sm font-medium text-foreground">
                {allValidations.length > 0
                  ? canApprove ? 'All fields found' : `${failed} field(s) not found`
                  : 'No validation results'}
              </span>
            </div>
            <div className="flex items-center gap-2">
              {passed   > 0 && <span className="text-xs text-green-600 font-medium">{passed} found</span>}
              {warnings > 0 && <span className="text-xs text-amber-600 font-medium">{warnings} warnings</span>}
              {failed   > 0 && <span className="text-xs text-red-600 font-medium">{failed} not found</span>}
              {showSummary ? <ChevronUp className="h-4 w-4 text-muted-foreground" /> : <ChevronDown className="h-4 w-4 text-muted-foreground" />}
            </div>
          </button>
          <button
            onClick={runAllValidations}
            disabled={isValidating}
            title="Run full validation"
            className="flex items-center gap-1 px-3 py-3 text-xs text-muted-foreground hover:text-foreground hover:bg-muted/50 transition-colors border-l border-border"
          >
            <RefreshCw className={`h-3.5 w-3.5 ${isValidating ? 'animate-spin' : ''}`} />
            {isValidating ? 'Validating…' : 'Run Validation'}
          </button>
        </div>
      </div>


      {/* ── Field content ─────────────────────────────────────────────── */}
      <div className="flex-1 overflow-auto">
        {fields.length === 0 ? (
          <div className="p-4">
            {rawText ? (
              <>
                <p className="text-xs text-muted-foreground mb-2">
                  No structured fields extracted — no document type or field config assigned.
                </p>
                <pre className="text-xs text-foreground bg-muted border border-border rounded p-3 whitespace-pre-wrap break-words max-h-[60vh] overflow-auto">
                  {rawText}
                </pre>
              </>
            ) : (
              <p className="text-sm text-muted-foreground text-center py-6">No fields extracted yet.</p>
            )}
          </div>
        ) : (
          <div className="space-y-0">

            {/* Edit hint */}
            <div className="px-4 py-2 bg-muted/50 border-b border-border">
              <p className="text-xs text-muted-foreground flex items-center gap-1">
                <Pencil className="h-3 w-3" /> Click any value to edit inline
              </p>
            </div>

            {/* ── Header Fields ─────────────────────────────────────── */}
            {headerFields.length > 0 && (
              <div>
                <div className="px-4 py-2 bg-blue-50 border-b border-border">
                  <p className="text-xs font-semibold text-blue-700 uppercase tracking-wide">Header Fields</p>
                </div>
                <div className="divide-y divide-border">
                  {headerFields.map(field => {
                    const label = fieldConfigMap[field.fieldName.toLowerCase()]?.displayLabel ?? field.fieldName;
                    const status = getValidationStatus(field.id);
                    const isPending = pendingIds.has(field.id);
                    const isLow = (field.confidence ?? 1) < 0.7;
                    const isSelected = selectedFieldId === field.id;
                    return (
                      <div
                        key={field.id}
                        className={`px-4 py-2.5 cursor-pointer transition-colors border-l-2 ${
                          isSelected       ? 'bg-primary/10 border-l-primary'
                          : isPending      ? 'bg-blue-50/40 border-l-blue-300'
                          : status === 'Failed'  ? 'bg-red-50 border-l-red-400'
                          : status === 'Warning' ? 'bg-amber-50 border-l-amber-400'
                          : status === 'Passed'  ? 'bg-green-50/50 border-l-green-400'
                          : isLow          ? 'bg-red-50/30 border-l-transparent'
                          : 'border-l-transparent hover:bg-muted/30'
                        }`}
                        onClick={() => onFieldSelect?.(isSelected ? null : field)}
                      >
                        <div className="flex items-center gap-1.5 mb-1">
                          <span className="text-xs font-medium text-muted-foreground uppercase tracking-wide flex-1">{label}</span>
                          {isPending        && <Loader2       className="h-3 w-3 text-blue-500 animate-spin" />}
                          {!isPending && status === 'Failed'  && <XCircle       className="h-3 w-3 text-red-500" />}
                          {!isPending && status === 'Warning' && <AlertTriangle  className="h-3 w-3 text-amber-500" />}
                          {!isPending && status === 'Passed'  && <CheckCircle    className="h-3 w-3 text-green-500" />}
                          <ConfBadge value={field.confidence ?? 0} />
                        </div>
                        <div onClick={e => e.stopPropagation()}>
                          <InlineEditCell field={field} onSave={save} />
                        </div>
                        {allValidations.filter(v => v.extractedFieldId === field.id && v.message).map(v => {
                          const label = v.status === 'Passed' ? '✓ In ERP'
                            : v.status === 'Warning' ? '⚠ Review'
                            : '✗ Not found';
                          return (
                            <div key={v.id} className="mt-0.5 space-y-0.5">
                              <span
                                className={`text-xs px-1.5 py-0.5 rounded font-medium font-mono ${
                                  v.status === 'Passed' ? 'bg-green-100 text-green-700'
                                  : v.status === 'Warning' ? 'bg-amber-100 text-amber-700'
                                  : 'bg-red-100 text-red-700'
                                }`}
                              >
                                {label}
                              </span>
                              {v.message && (
                                <p className={`text-xs leading-tight ${
                                  v.status === 'Passed' ? 'text-green-600'
                                  : v.status === 'Warning' ? 'text-amber-600'
                                  : 'text-red-600'
                                }`}>{v.message}</p>
                              )}
                            </div>
                          );
                        })}
                      </div>
                    );
                  })}
                </div>
              </div>
            )}

            {/* ── Table Fields ──────────────────────────────────────── */}
            {tableRowCount > 0 && (
              <div>
                <div className="px-4 py-2 bg-purple-50 border-y border-border">
                  <p className="text-xs font-semibold text-purple-700 uppercase tracking-wide">
                    Table Data <span className="font-normal text-purple-500">({tableRowCount} rows)</span>
                  </p>
                </div>
                <div className="overflow-x-auto">
                  <table className="w-full text-sm">
                    <thead>
                      <tr className="border-b border-border bg-muted/30">
                        <th className="text-center px-2 py-2 font-medium text-muted-foreground text-xs">#</th>
                        {tableColumnNames.map(name => (
                          <th key={name} className="text-left px-2 py-2 font-medium text-muted-foreground text-xs whitespace-nowrap">
                            {fieldConfigMap[name.toLowerCase()]?.displayLabel ?? name}
                          </th>
                        ))}
                        <th className="px-2 py-2 text-muted-foreground text-xs">Valid.</th>
                        <th className="px-2 py-2"></th>
                      </tr>
                    </thead>
                    <tbody className="divide-y divide-border">
                      {Array.from({ length: tableRowCount }, (_, i) => {
                        const rowConfs = tableColumnNames
                          .map(n => tableFieldGroups[n]?.[i]?.confidence)
                          .filter((c): c is number => c !== undefined);
                        const avgConf = rowConfs.length > 0 ? rowConfs.reduce((s, c) => s + c, 0) / rowConfs.length : 1;
                        const firstField = tableFieldGroups[tableColumnNames[0]]?.[i];
                        // Collect all field IDs for this row (for delete)
                        const rowFieldIds = tableColumnNames
                          .map(n => tableFieldGroups[n]?.[i]?.id)
                          .filter((id): id is string => !!id);
                        // Validation status for this specific row — match by extractedFieldId
                        // so each row gets its own result, not an aggregate of all rows.
                        const rowValidations = allValidations.filter(v =>
                          v.extractedFieldId && rowFieldIds.includes(v.extractedFieldId));
                        const rowIsPending = rowFieldIds.some(id => pendingIds.has(id));
                        const rowStatus = rowValidations.some(v => v.status === 'Failed') ? 'Failed'
                          : rowValidations.some(v => v.status === 'Warning') ? 'Warning'
                          : rowValidations.some(v => v.status === 'Passed') ? 'Passed'
                          : null;
                        const passedV = rowValidations.find(v => v.status === 'Passed' && v.erpResponseField);
                        const passedValue = passedV?.erpResponseField
                          ? getErpValue(passedV.erpReference, passedV.erpResponseField)
                          : undefined;
                        const passedSuccessLabel = passedValue ? `✓ ${passedV!.erpResponseField}: ${passedValue}` : '✓';
                        return (
                          <tr
                            key={i}
                            className={`hover:bg-muted/30 cursor-pointer ${avgConf < 0.7 ? 'bg-red-50/50' : ''}`}
                            onClick={() => firstField && onFieldSelect?.(selectedFieldId === firstField.id ? null : firstField)}
                          >
                            <td className="px-2 py-2 text-center text-muted-foreground text-xs">{i + 1}</td>
                            {tableColumnNames.map(name => {
                              const cellField = tableFieldGroups[name]?.[i];
                              const cellStatus = cellField ? getValidationStatus(cellField.id) : null;
                              const cellPending = cellField ? pendingIds.has(cellField.id) : false;
                              return (
                                <td
                                  key={name}
                                  className={`px-2 py-2 transition-colors ${
                                    cellPending      ? 'bg-blue-50'
                                    : cellStatus === 'Failed'  ? 'bg-red-100/80 border-b border-red-300'
                                    : cellStatus === 'Warning' ? 'bg-amber-50/80 border-b border-amber-200'
                                    : cellStatus === 'Passed'  ? 'bg-green-50/60'
                                    : ''
                                  }`}
                                  onClick={e => e.stopPropagation()}
                                >
                                  {cellField
                                    ? <InlineEditCell field={cellField} onSave={save} />
                                    : <span className="text-muted-foreground">—</span>}
                                </td>
                              );
                            })}
                            {/* Valid. column */}
                            <td className="px-2 py-2 text-center whitespace-nowrap">
                              {rowIsPending ? (
                                <span className="inline-flex items-center gap-1 text-xs text-blue-500">
                                  <Loader2 className="h-3 w-3 animate-spin" /> Checking
                                </span>
                              ) : rowStatus === 'Failed' ? (
                                <span className="text-xs font-medium text-red-600">✗ Not found</span>
                              ) : rowStatus === 'Warning' ? (
                                <span className="text-xs font-medium text-amber-600">⚠ Review</span>
                              ) : rowStatus === 'Passed' ? (
                                <span className="text-xs font-medium text-green-600 font-mono">{passedSuccessLabel}</span>
                              ) : (
                                <span className="text-xs text-muted-foreground/40">—</span>
                              )}
                            </td>
                            <td className="px-2 py-2 text-center" onClick={e => e.stopPropagation()}>
                              <button
                                onClick={() => deleteRow.mutate(rowFieldIds)}
                                disabled={deleteRow.isPending}
                                className="p-1 rounded text-muted-foreground hover:text-red-600 hover:bg-red-50 transition-colors"
                                title="Delete row"
                              >
                                <Trash2 className="h-3.5 w-3.5" />
                              </button>
                            </td>
                          </tr>
                        );
                      })}
                    </tbody>
                  </table>
                </div>
              </div>
            )}
          </div>
        )}
      </div>
    </div>
  );
}
