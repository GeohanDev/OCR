import { useState, useRef, useEffect, useCallback, useMemo } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { ocrApi, validationApi, documentApi, erpApi } from '../api/client';
// validationApi.run is intentionally unused here — Run Validation fires per-field mutations instead.
import type { ExtractedField, ValidationResult, FieldMappingConfig } from '../types';
import { CheckCircle, XCircle, AlertTriangle, ChevronDown, ChevronUp, Pencil, Trash2, RefreshCw, Loader2, StopCircle, PlusCircle, ExternalLink } from 'lucide-react';

// ── Acumatica deep-link builder ───────────────────────────────────────────────
// Constructs a "Main?ScreenId=..." URL to the exact ERP record that was used
// for validation, so reviewers can open it with one click.
function buildAcumaticaUrl(baseUrl: string, validationType: string, erpRef: unknown): string | null {
  if (!baseUrl || !erpRef || typeof erpRef !== 'object') return null;
  const ref = erpRef as Record<string, unknown>;

  switch (validationType) {
    case 'ErpApInvoice': {
      const refNbr = ref['RefNbr'] as string | undefined;
      const docType = (ref['DocType'] as string | undefined) ?? 'INV';
      const vendorId = ref['VendorId'] as string | undefined;
      if (!refNbr) return null;
      const vendor = vendorId ? `&VendorID=${encodeURIComponent(vendorId)}` : '';
      return `${baseUrl}/Main?ScreenId=AP301000&DocType=${encodeURIComponent(docType)}&RefNbr=${encodeURIComponent(refNbr)}${vendor}`;
    }
    case 'DynamicErp': {
      const refNbr = ref['ReferenceNbr'] as string | undefined;
      const type = (ref['Type'] as string | undefined) ?? 'INV';
      const vendorId = ref['Vendor'] as string | undefined;
      if (!refNbr) return null;
      const vendor = vendorId ? `&VendorID=${encodeURIComponent(vendorId)}` : '';
      return `${baseUrl}/Main?ScreenId=AP301000&DocType=${encodeURIComponent(type)}&RefNbr=${encodeURIComponent(refNbr)}${vendor}`;
    }
    case 'ErpVendor':
    case 'ErpVendorName': {
      const vendorId = ref['VendorId'] as string | undefined;
      if (!vendorId) return null;
      return `${baseUrl}/Main?ScreenId=AP303000&AcctCD=${encodeURIComponent(vendorId)}`;
    }
    case 'ErpBranch': {
      const branchId = ref['BranchId'] as string | undefined;
      if (!branchId) return null;
      return `${baseUrl}/Main?ScreenId=GL102000&BranchCD=${encodeURIComponent(branchId)}`;
    }
    case 'ErpCurrency': {
      const code = ref['CurrencyCode'] as string | undefined;
      if (!code) return null;
      return `${baseUrl}/Main?ScreenId=CM202000&CuryID=${encodeURIComponent(code)}`;
    }
    default:
      return null;
  }
}

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
  const displayVal = field.isManuallyCorreected && field.correctedValue != null
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

  const { data: acumaticaConfig } = useQuery({
    queryKey: ['erp', 'acumatica-base-url'],
    queryFn: () => erpApi.getAcumaticaBaseUrl().then(r => r.data),
    staleTime: Infinity,
    retry: false,
  });
  const acumaticaBaseUrl = acumaticaConfig?.baseUrl ?? '';

  // Per-field validation: only re-validates the changed field and surgically
  // updates the cache — other fields' results are preserved.
  const validateField = useMutation({
    mutationFn: (fieldId: string) =>
      validationApi.validateField(documentId, fieldId).then(r => r.data as ValidationResult[]),
    onMutate: (fieldId) => {
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
  });

  const allFieldIds = useMemo(() => fields.map(f => f.id), [fields]);
  // Tracks whether the sequential "Run Validation" loop is running (for button state).
  const [isRunning, setIsRunning] = useState(false);
  const stopRequestedRef = useRef(false);

  const setValidating = useCallback((flag: boolean) => {
    documentApi.setValidating(documentId, flag).catch(() => {});
  }, [documentId]);

  const correctField = useMutation({
    mutationFn: ({ fieldId, value }: { fieldId: string; value: string }) =>
      ocrApi.correctField(documentId, fieldId, value),
    onSuccess: (_, { fieldId }) => {
      queryClient.invalidateQueries({ queryKey: ['ocr-result', documentId] });
      // Refresh document + list so vendorName corrections are reflected immediately.
      queryClient.invalidateQueries({ queryKey: ['document', documentId] });
      queryClient.invalidateQueries({ queryKey: ['documents'] });
      const rowIds = fieldToRowRef.current.get(fieldId);
      if (rowIds && rowIds.length > 1) {
        // Table cell: only re-validate siblings that have ERP mapping configured.
        // Non-ERP fields have no validator to run and should not show a spinner.
        const toValidate = rowIds.filter(id => validatableFieldIds.includes(id));
        toValidate.forEach(id => validateField.mutate(id));
      } else if (validatableFieldIds.includes(fieldId)) {
        validateField.mutate(fieldId);
      }
    },
  });

  // Per-row delete loading state: tracks the first fieldId of each row being deleted.
  const [deletingRowIds, setDeletingRowIds] = useState<Set<string>>(new Set());
  const [deleteError, setDeleteError] = useState<string | null>(null);

  // Delete all field IDs belonging to a table row (one per column at that index).
  const deleteRow = useMutation({
    mutationFn: (fieldIds: string[]) =>
      Promise.all(fieldIds.map(id => ocrApi.deleteField(documentId, id))),
    onMutate: (fieldIds) => {
      if (fieldIds[0]) setDeletingRowIds(prev => new Set([...prev, fieldIds[0]]));
      setDeleteError(null);
    },
    onSettled: (_data, _err, fieldIds) => {
      if (fieldIds[0]) setDeletingRowIds(prev => { const next = new Set(prev); next.delete(fieldIds[0]); return next; });
    },
    onSuccess: () => {
      // Invalidate both so the server re-fetches reflect the deleted field's
      // validation results being removed (backend deletes them on field delete).
      queryClient.invalidateQueries({ queryKey: ['ocr-result', documentId] });
      queryClient.invalidateQueries({ queryKey: ['validation', documentId] });
    },
    onError: (error: unknown) => {
      const msg = error instanceof Error ? error.message : 'Delete failed';
      setDeleteError(msg);
    },
  });

  const addTableRow = useMutation({
    mutationFn: (columns: { fieldName: string; fieldMappingConfigId?: string }[]) =>
      ocrApi.addTableRow(documentId, columns),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['ocr-result', documentId] });
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

  // Only fields with an ERP mapping key configured need a spinner.
  const validatableFieldIds = useMemo(() =>
    fields.filter(f => {
      const cfg = fieldConfigMap[f.fieldName.toLowerCase()];
      return cfg?.erpMappingKey && !cfg.isManualEntry;
    }).map(f => f.id),
  [fields, fieldConfigMap]);

  const validateRow = useCallback((fieldIds: string[]) => {
    const ids = fieldIds.filter(fid => validatableFieldIds.includes(fid));
    if (ids.length === 0) return;
    ids.forEach(fid => setPendingIds(prev => new Set([...prev, fid])));
    validationApi.validateRow(documentId, ids)
      .then(res => {
        const newResults = res.data as ValidationResult[];
        queryClient.setQueryData<ValidationResult[]>(['validation', documentId], old => {
          const base = old ?? [];
          const idSet = new Set(ids);
          return [...base.filter(v => !v.extractedFieldId || !idSet.has(v.extractedFieldId)), ...newResults];
        });
      })
      .catch(() => {})
      .finally(() => {
        ids.forEach(fid => setPendingIds(prev => { const next = new Set(prev); next.delete(fid); return next; }));
      });
  }, [documentId, validatableFieldIds, queryClient]);

  // Sequential validation with dependency-chain support:
  // - Header fields run one-by-one; if a field fails, dependents are skipped.
  // - Table rows run column-by-column with per-row AND global failure tracking.
  // Ensures vendor is validated before line items, and invoice ref before amount/balance.
  const runAllValidations = useCallback(async () => {
    stopRequestedRef.current = false;
    setIsRunning(true);
    setValidating(true);

    const validatable = fields.filter(f => {
      const cfg = fieldConfigMap[f.fieldName.toLowerCase()];
      return cfg?.erpMappingKey && !cfg.isManualEntry && !cfg.isCheckbox;
    });

    const checkIsTableField = (name: string) => {
      const cfg = fieldConfigMap[name.toLowerCase()];
      if (cfg) return cfg.allowMultiple;
      return fields.filter(f => f.fieldName === name).length > 1;
    };

    const headerFieldsToRun = validatable
      .filter(f => !checkIsTableField(f.fieldName))
      .sort((a, b) =>
        (fieldConfigMap[a.fieldName.toLowerCase()]?.displayOrder ?? 999) -
        (fieldConfigMap[b.fieldName.toLowerCase()]?.displayOrder ?? 999));
    const tableFields = validatable.filter(f => checkIsTableField(f.fieldName));

    const failedFieldNames = new Set<string>();

    for (const f of headerFieldsToRun) {
      if (stopRequestedRef.current) break;
      const cfg = fieldConfigMap[f.fieldName.toLowerCase()];
      if (cfg?.dependentFieldKey && failedFieldNames.has(cfg.dependentFieldKey.toLowerCase())) continue;
      try {
        const results = await validateField.mutateAsync(f.id);
        if (results.some((r: ValidationResult) => r.status === 'Failed'))
          failedFieldNames.add(f.fieldName.toLowerCase());
      } catch { /* onError handles 424 */ }
    }

    const colNames = [...new Set(tableFields.map(f => f.fieldName))];
    const groupedCols: Record<string, ExtractedField[]> = {};
    for (const name of colNames) groupedCols[name] = fields.filter(f => f.fieldName === name);
    const maxRows = colNames.length > 0 ? Math.max(...Object.values(groupedCols).map(g => g.length), 0) : 0;

    // 2. Run all table rows in parallel using ValidateRowAsync (one ERP lookup per row).
    if (maxRows > 0) {
      const sortedCols = [...colNames].sort((a, b) =>
        (fieldConfigMap[a.toLowerCase()]?.displayOrder ?? 999) -
        (fieldConfigMap[b.toLowerCase()]?.displayOrder ?? 999));
      const rowPromises = Array.from({ length: maxRows }, (_, i) => {
        const rowFields = sortedCols
          .map(name => groupedCols[name]?.[i])
          .filter((f): f is ExtractedField => f !== undefined);
        const ids = rowFields
          .filter(f => fieldConfigMap[f.fieldName.toLowerCase()]?.erpMappingKey &&
                       !fieldConfigMap[f.fieldName.toLowerCase()]?.isManualEntry &&
                       !fieldConfigMap[f.fieldName.toLowerCase()]?.isCheckbox)
          .map(f => f.id);
        if (ids.length === 0) return Promise.resolve();
        ids.forEach(fid => setPendingIds(prev => new Set([...prev, fid])));
        return validationApi.validateRow(documentId, ids)
          .then(res => {
            const newResults = res.data as ValidationResult[];
            queryClient.setQueryData<ValidationResult[]>(['validation', documentId], old => {
              const base = old ?? [];
              const idSet = new Set(ids);
              return [...base.filter(v => !v.extractedFieldId || !idSet.has(v.extractedFieldId)), ...newResults];
            });
          })
          .catch(() => {})
          .finally(() => {
            ids.forEach(fid => setPendingIds(prev => { const next = new Set(prev); next.delete(fid); return next; }));
          });
      });
      await Promise.all(rowPromises);
    }

    setValidating(false);
    setIsRunning(false);
  }, [fields, fieldConfigMap, validateField, setValidating, documentId, queryClient]);

  const stopValidation = useCallback(() => {
    stopRequestedRef.current = true;
    setIsRunning(false);
    setPendingIds(new Set());
    setValidating(false);
  }, [setValidating]);

  const isValidating = isRunning || allFieldIds.some(id => pendingIds.has(id));

  // Stop the running validation loop when documentId changes (user navigates to a different
  // document while this component is reused by React Router without unmounting).
  useEffect(() => {
    return () => {
      stopRequestedRef.current = true;
      setIsRunning(false);
      setPendingIds(new Set());
      documentApi.setValidating(documentId, false).catch(() => {});
    };
  }, [documentId]);

  const PAGE_SIZE = 30;
  const [tablePage, setTablePage] = useState(1);

  // Reset pagination to page 1 whenever the document changes.
  useEffect(() => { setTablePage(1); }, [documentId]);

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

  const totalPages = Math.ceil(tableRowCount / PAGE_SIZE);
  const pageStart = (tablePage - 1) * PAGE_SIZE;  // inclusive index
  const pageEnd   = Math.min(tablePage * PAGE_SIZE, tableRowCount);  // exclusive index

  // AllowMultiple configs — used to build the "+ Add Row" column list even when no rows exist yet.
  const tableConfigColumns = useMemo(() =>
    (fieldConfigs ?? []).filter(c => c.allowMultiple),
  [fieldConfigs]);

  // Compute which table-row field IDs belong to "settled" rows (any isCheckbox
  // column in that row has correctedValue / normalizedValue === "true").
  const settledFieldIds = useMemo(() => {
    const ids = new Set<string>();
    for (let i = 0; i < tableRowCount; i++) {
      const rowIsSettled = tableColumnNames.some(name => {
        const cfg = fieldConfigMap[name.toLowerCase()];
        if (!cfg?.isCheckbox) return false;
        const f = tableFieldGroups[name]?.[i];
        if (!f) return false;
        const val = f.isManuallyCorreected ? f.correctedValue : (f.normalizedValue ?? f.rawValue);
        return val === 'true';
      });
      if (rowIsSettled) {
        tableColumnNames.forEach(name => {
          const f = tableFieldGroups[name]?.[i];
          if (f?.id) ids.add(f.id);
        });
      }
    }
    return ids;
  }, [tableColumnNames, tableFieldGroups, tableRowCount, fieldConfigMap]);

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
          {isValidating ? (
            <button
              onClick={stopValidation}
              title="Stop validation"
              className="flex items-center gap-1 px-3 py-3 text-xs text-destructive hover:bg-destructive/10 transition-colors border-l border-border"
            >
              <StopCircle className="h-3.5 w-3.5" />
              Stop
            </button>
          ) : (
            <button
              onClick={runAllValidations}
              title="Run full validation"
              className="flex items-center gap-1 px-3 py-3 text-xs text-muted-foreground hover:text-foreground hover:bg-muted/50 transition-colors border-l border-border"
            >
              <RefreshCw className="h-3.5 w-3.5" />
              Run Validation
            </button>
          )}
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
                <div className="px-4 py-2 bg-muted/30 border-b border-border">
                  <p className="text-xs font-semibold text-muted-foreground uppercase tracking-wide">Header Fields</p>
                </div>
                <div className="divide-y divide-border">
                  {headerFields.map(field => {
                    const cfg = fieldConfigMap[field.fieldName.toLowerCase()];
                    const label = cfg?.displayLabel ?? field.fieldName;
                    const isManual = cfg?.isManualEntry ?? false;
                    const status = getValidationStatus(field.id);
                    const isPending = pendingIds.has(field.id);
                    const isLow = !isManual && (field.confidence ?? 1) < 0.7;
                    const isSelected = selectedFieldId === field.id;
                    return (
                      <div
                        key={field.id}
                        className={`px-4 py-2.5 cursor-pointer transition-colors border-l-2 ${
                          isSelected       ? 'bg-primary/10 border-l-primary'
                          : isManual       ? 'bg-violet-50/50 border-l-violet-400'
                          : isPending      ? 'bg-muted/40 border-l-muted-foreground/30'
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
                          {isManual && <span className="text-[10px] px-1.5 py-0.5 rounded-full bg-violet-100 text-violet-700 font-medium">Manual</span>}
                          {!isManual && isPending        && <Loader2       className="h-3 w-3 text-muted-foreground animate-spin" />}
                          {!isManual && !isPending && status === 'Failed'  && <XCircle       className="h-3 w-3 text-red-500" />}
                          {!isManual && !isPending && status === 'Warning' && <AlertTriangle  className="h-3 w-3 text-amber-500" />}
                          {!isManual && !isPending && status === 'Passed'  && <CheckCircle    className="h-3 w-3 text-green-500" />}
                          {!isManual && <ConfBadge value={field.confidence ?? 0} />}
                        </div>
                        <div onClick={e => e.stopPropagation()}>
                          <InlineEditCell field={field} onSave={save} />
                        </div>
                        {isManual && !field.correctedValue && !field.normalizedValue && !field.rawValue && (
                          <p className="text-[10px] text-violet-500 mt-0.5 italic">Enter value manually above</p>
                        )}
                        {allValidations.filter(v => v.extractedFieldId === field.id && v.message && v.status === status).map(v => {
                          const isMismatch = v.message?.toLowerCase().includes('mismatch');
                          const erpPassValue = v.status === 'Passed' && v.erpResponseField
                            ? getErpValue(v.erpReference, v.erpResponseField)
                            : undefined;
                          const badgeLabel = v.status === 'Passed'
                            ? (erpPassValue ? `✓ ${v.erpResponseField}: ${erpPassValue}` : '✓ In ERP')
                            : v.status === 'Warning'
                              ? (isMismatch ? '⚠ Value mismatch' : v.message?.toLowerCase().includes('not found') ? '⚠ Not found in ERP' : '⚠ Review')
                            : '✗ Not found in ERP';
                          const erpSuggestion = (v.status === 'Warning' || v.status === 'Failed') && v.erpResponseField
                            ? getErpValue(v.erpReference, v.erpResponseField)
                            : undefined;
                          const erpLink = buildAcumaticaUrl(acumaticaBaseUrl, v.validationType, v.erpReference);
                          return (
                            <div key={v.id} className="mt-0.5 space-y-0.5">
                              <div className="flex items-center gap-1 flex-wrap">
                                <span className={`text-xs px-1.5 py-0.5 rounded font-medium font-mono ${
                                  v.status === 'Passed' ? 'bg-green-100 text-green-700'
                                  : v.status === 'Warning' ? 'bg-amber-100 text-amber-700'
                                  : 'bg-red-100 text-red-700'
                                }`}>{badgeLabel}</span>
                                {erpLink && (
                                  <a
                                    href={erpLink}
                                    target="_blank"
                                    rel="noopener noreferrer"
                                    title="Open in Acumatica"
                                    className="inline-flex items-center gap-0.5 text-[10px] text-blue-500 hover:text-blue-700 hover:underline"
                                  >
                                    <ExternalLink className="h-2.5 w-2.5" />
                                    Acumatica
                                  </a>
                                )}
                              </div>
                              {v.message && (
                                <p className={`text-xs leading-tight ${
                                  v.status === 'Passed' ? 'text-green-600'
                                  : v.status === 'Warning' ? 'text-amber-600'
                                  : 'text-red-600'
                                }`}>{v.message}</p>
                              )}
                              {erpSuggestion && (
                                <p className="text-xs text-muted-foreground leading-tight">
                                  → Correct value in ERP: <span className="font-mono font-medium">{erpSuggestion}</span>
                                </p>
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
            {(tableRowCount > 0 || tableConfigColumns.length > 0) && (
              <div>
                {deleteError && (
                  <div className="mx-4 mt-2 px-3 py-2 bg-red-50 border border-red-200 rounded text-xs text-red-700 flex items-center justify-between">
                    <span>Delete failed: {deleteError}</span>
                    <button onClick={() => setDeleteError(null)} className="ml-2 text-red-400 hover:text-red-600">✕</button>
                  </div>
                )}
                <div className="px-4 py-2 bg-purple-50 border-y border-border flex items-center justify-between">
                  <p className="text-xs font-semibold text-purple-700 uppercase tracking-wide">
                    Table Data <span className="font-normal text-purple-500">({tableRowCount} rows)</span>
                  </p>
                  {totalPages > 1 && (
                    <div className="flex items-center gap-2">
                      <button
                        onClick={() => setTablePage(p => Math.max(1, p - 1))}
                        disabled={tablePage === 1}
                        className="px-2 py-0.5 text-xs rounded border border-border bg-background text-foreground hover:bg-muted disabled:opacity-40 disabled:cursor-not-allowed transition-colors"
                      >
                        ← Prev
                      </button>
                      <span className="text-xs text-purple-600 font-medium whitespace-nowrap">
                        Page {tablePage} of {totalPages}
                      </span>
                      <button
                        onClick={() => setTablePage(p => Math.min(totalPages, p + 1))}
                        disabled={tablePage === totalPages}
                        className="px-2 py-0.5 text-xs rounded border border-border bg-background text-foreground hover:bg-muted disabled:opacity-40 disabled:cursor-not-allowed transition-colors"
                      >
                        Next →
                      </button>
                    </div>
                  )}
                </div>
                <div>
                  {tableRowCount === 0 && (
                    <p className="px-4 py-3 text-xs text-muted-foreground italic">No rows yet. Click below to add one.</p>
                  )}
                  <table className="w-full text-sm">
                    <thead>
                      <tr className="border-b border-border bg-muted/30">
                        <th className="text-center px-2 py-2 font-medium text-muted-foreground text-xs">#</th>
                        {tableColumnNames.map(name => {
                          const isCheckboxCol = fieldConfigMap[name.toLowerCase()]?.isCheckbox ?? false;
                          return (
                            <th key={name} className={`${isCheckboxCol ? 'text-center' : 'text-left'} px-2 py-2 font-medium text-muted-foreground text-xs whitespace-nowrap`}>
                              {fieldConfigMap[name.toLowerCase()]?.displayLabel ?? name}
                            </th>
                          );
                        })}
                        <th className="px-2 py-2 text-muted-foreground text-xs">Valid.</th>
                        <th className="px-2 py-2 text-muted-foreground text-xs"></th>
                        <th className="px-2 py-2"></th>
                      </tr>
                    </thead>
                    <tbody className="divide-y divide-border">
                      {Array.from({ length: pageEnd - pageStart }, (_, idx) => {
                        const i = pageStart + idx;
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
                        const isSettled = rowFieldIds.some(id => settledFieldIds.has(id));
                        return (
                          <tr
                            key={i}
                            className={`hover:bg-muted/30 cursor-pointer transition-opacity ${isSettled ? 'opacity-50' : ''} ${avgConf < 0.7 && !isSettled ? 'bg-red-50/50' : ''}`}
                            onClick={() => firstField && onFieldSelect?.(selectedFieldId === firstField.id ? null : firstField)}
                          >
                            <td className="px-2 py-2 text-center text-muted-foreground text-xs">{i + 1}</td>
                            {tableColumnNames.map(name => {
                              const cellField = tableFieldGroups[name]?.[i];
                              const cellCfg = fieldConfigMap[name.toLowerCase()];
                              const isManualCol = cellCfg?.isCheckbox ?? false;

                              // Render a checkbox toggle for isCheckbox columns
                              if (isManualCol) {
                                const rawVal = cellField
                                  ? (cellField.isManuallyCorreected ? cellField.correctedValue : (cellField.normalizedValue ?? cellField.rawValue))
                                  : undefined;
                                const checked = rawVal === 'true';
                                return (
                                  <td key={name} className="px-2 py-2 text-center" onClick={e => e.stopPropagation()}>
                                    {cellField ? (
                                      <input
                                        type="checkbox"
                                        checked={checked}
                                        onChange={e => save(cellField.id, e.target.checked ? 'true' : 'false')}
                                        className="h-4 w-4 rounded border-gray-300 accent-violet-600 cursor-pointer"
                                        title={cellCfg?.displayLabel ?? name}
                                      />
                                    ) : <span className="text-muted-foreground">—</span>}
                                  </td>
                                );
                              }

                              const cellStatus = cellField ? getValidationStatus(cellField.id) : null;
                              const cellPending = cellField ? pendingIds.has(cellField.id) : false;
                              const isCreditCol = /credit|payment/i.test(name);
                              const debitColName = isCreditCol
                                ? tableColumnNames.find(n => /amount|debit/i.test(n) && !/credit|payment/i.test(n))
                                : undefined;
                              const debitField = debitColName ? tableFieldGroups[debitColName]?.[i] : undefined;
                              const debitValue = debitField
                                ? (debitField.isManuallyCorreected && debitField.correctedValue != null
                                    ? debitField.correctedValue
                                    : (debitField.normalizedValue ?? debitField.rawValue ?? ''))
                                : '';
                              return (
                                <td
                                  key={name}
                                  className={`px-2 py-2 transition-colors ${
                                    cellPending      ? 'bg-muted/40'
                                    : cellStatus === 'Failed'  ? 'bg-red-100/80 border-b border-red-300'
                                    : cellStatus === 'Warning' ? 'bg-amber-50/80 border-b border-amber-200'
                                    : cellStatus === 'Passed'  ? 'bg-green-50/60'
                                    : ''
                                  }`}
                                  onClick={e => e.stopPropagation()}
                                >
                                  {cellField
                                    ? (
                                      <div className="flex items-center gap-1">
                                        <InlineEditCell field={cellField} onSave={save} />
                                        {isCreditCol && debitField && debitValue && (
                                          <button
                                            onClick={e => { e.stopPropagation(); save(cellField.id, debitValue); }}
                                            title={`Mirror debit amount: ${debitValue}`}
                                            className="flex-shrink-0 text-[10px] px-1 py-0.5 rounded text-blue-600 hover:bg-blue-50 border border-blue-200 leading-none"
                                          >
                                            ↕
                                          </button>
                                        )}
                                      </div>
                                    )
                                    : <span className="text-muted-foreground">—</span>}
                                </td>
                              );
                            })}
                            {/* Valid. column */}
                            <td className="px-2 py-2 text-center whitespace-nowrap">
                              {isSettled ? (
                                <span className="text-xs font-medium text-violet-600 px-1.5 py-0.5 bg-violet-50 rounded">Settled</span>
                              ) : rowIsPending ? (
                                <span className="inline-flex items-center gap-1 text-xs text-muted-foreground">
                                  <Loader2 className="h-3 w-3 animate-spin" /> Checking
                                </span>
                              ) : rowStatus === 'Failed' ? (
                                <span
                                  className="text-xs font-medium text-red-600"
                                  title={rowValidations.filter(v => v.status === 'Failed' && v.message).map(v => v.message).join('\n') || undefined}
                                >
                                  ✗ Not found
                                </span>
                              ) : rowStatus === 'Warning' ? (
                                <span
                                  className="text-xs font-medium text-amber-600"
                                  title={rowValidations.filter(v => v.status === 'Warning' && v.message).map(v => {
                                    const sug = v.erpResponseField ? getErpValue(v.erpReference, v.erpResponseField) : undefined;
                                    return sug ? `${v.message} → ERP: ${sug}` : v.message;
                                  }).join('\n') || undefined}
                                >
                                  ⚠ Review
                                </span>
                              ) : rowStatus === 'Passed' ? (
                                <span className="text-xs font-medium text-green-600 font-mono">{passedSuccessLabel}</span>
                              ) : (
                                <span className="text-xs text-muted-foreground/40">—</span>
                              )}
                            </td>
                            <td className="px-2 py-1" onClick={e => e.stopPropagation()}>
                              <button
                                onClick={() => {
                                  const ids = tableColumnNames
                                    .map(name => tableFieldGroups[name]?.[i])
                                    .filter((f): f is ExtractedField => f !== undefined)
                                    .filter(f => validatableFieldIds.includes(f.id))
                                    .map(f => f.id);
                                  validateRow(ids);
                                }}
                                disabled={tableColumnNames.some(name => {
                                  const f = tableFieldGroups[name]?.[i];
                                  return f ? pendingIds.has(f.id) : false;
                                })}
                                className="text-muted-foreground hover:text-foreground disabled:opacity-40"
                                title="Validate this row"
                              >
                                {tableColumnNames.some(name => { const f = tableFieldGroups[name]?.[i]; return f ? pendingIds.has(f.id) : false; })
                                  ? <Loader2 className="h-3.5 w-3.5 animate-spin" />
                                  : <RefreshCw className="h-3.5 w-3.5" />}
                              </button>
                            </td>
                            <td className="px-2 py-2 text-center" onClick={e => e.stopPropagation()}>
                              {rowFieldIds[0] && deletingRowIds.has(rowFieldIds[0]) ? (
                                <Loader2 className="h-3.5 w-3.5 animate-spin text-gray-400 mx-auto" />
                              ) : (
                                <button
                                  onClick={() => deleteRow.mutate(rowFieldIds)}
                                  className="p-1 rounded text-muted-foreground hover:text-red-600 hover:bg-red-50 transition-colors"
                                  title="Delete row"
                                >
                                  <Trash2 className="h-3.5 w-3.5" />
                                </button>
                              )}
                            </td>
                          </tr>
                        );
                      })}
                    </tbody>
                  </table>
                  {totalPages > 1 && (
                    <div className="border-t border-border px-3 py-2 flex items-center justify-between bg-muted/20">
                      <span className="text-xs text-muted-foreground">
                        Rows {pageStart + 1}–{pageEnd} of {tableRowCount}
                      </span>
                      <div className="flex items-center gap-2">
                        <button
                          onClick={() => setTablePage(p => Math.max(1, p - 1))}
                          disabled={tablePage === 1}
                          className="px-2 py-0.5 text-xs rounded border border-border bg-background text-foreground hover:bg-muted disabled:opacity-40 disabled:cursor-not-allowed transition-colors"
                        >
                          ← Prev
                        </button>
                        <span className="text-xs text-muted-foreground font-medium whitespace-nowrap">
                          Page {tablePage} of {totalPages}
                        </span>
                        <button
                          onClick={() => setTablePage(p => Math.min(totalPages, p + 1))}
                          disabled={tablePage === totalPages}
                          className="px-2 py-0.5 text-xs rounded border border-border bg-background text-foreground hover:bg-muted disabled:opacity-40 disabled:cursor-not-allowed transition-colors"
                        >
                          Next →
                        </button>
                      </div>
                    </div>
                  )}
                  <div className="border-t border-border px-3 py-2">
                    <button
                      onClick={() => {
                        const cols = tableConfigColumns.length > 0
                          ? tableConfigColumns.map(c => ({ fieldName: c.fieldName, fieldMappingConfigId: c.id }))
                          : tableColumnNames.map(name => ({ fieldName: name, fieldMappingConfigId: fieldConfigMap[name.toLowerCase()]?.id }));
                        addTableRow.mutate(cols);
                      }}
                      disabled={addTableRow.isPending}
                      className="flex items-center gap-1.5 text-xs text-primary hover:text-primary/80 disabled:opacity-50 transition-colors"
                    >
                      {addTableRow.isPending
                        ? <Loader2 className="h-3.5 w-3.5 animate-spin" />
                        : <PlusCircle className="h-3.5 w-3.5" />}
                      Add Row
                    </button>
                  </div>
                </div>
              </div>
            )}
          </div>
        )}
      </div>
    </div>
  );
}
