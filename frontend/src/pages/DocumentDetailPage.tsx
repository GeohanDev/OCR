import { useState, useRef, useEffect, useCallback, useMemo } from 'react';
import { useParams, useNavigate, Link } from 'react-router-dom';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { documentApi, ocrApi, validationApi, configApi } from '../api/client';
// validationApi used for getResults + auto-revalidate after field edits
import { useAuth } from '../contexts/AuthContext';
import StatusBadge from '../components/ui/StatusBadge';
import type { Document, OcrResult, DocumentType, ExtractedField, FieldMappingConfig, ValidationResult } from '../types';
import {
  ChevronLeft, Cpu, XCircle, FileText,
  AlertTriangle, CheckCircle, Loader2, Clock, History, Edit2, Check, X, Pencil, CheckSquare, RefreshCw, StopCircle, BarChart2,
} from 'lucide-react';

// ─── EditableCell ─────────────────────────────────────────────────────────────
// Ported from StatementSync — click to edit, Enter to save, Escape to cancel.

interface EditableCellProps {
  field: ExtractedField | undefined;
  onSave: (fieldId: string, value: string) => void;
  placeholder?: string;
  className?: string;
  align?: 'left' | 'right' | 'center';
}

function EditableCell({
  field,
  onSave,
  placeholder = '—',
  className = '',
  align = 'left',
}: EditableCellProps) {
  const displayVal = field
    ? (field.isManuallyCorreected && field.correctedValue
        ? field.correctedValue
        : (field.normalizedValue ?? field.rawValue ?? ''))
    : '';

  const [editing, setEditing] = useState(false);
  const [draft, setDraft] = useState(displayVal);
  const inputRef = useRef<HTMLInputElement>(null);

  useEffect(() => { setDraft(displayVal); }, [displayVal]);

  useEffect(() => {
    if (editing && inputRef.current) {
      inputRef.current.focus();
      inputRef.current.select();
    }
  }, [editing]);

  const save = useCallback(() => {
    setEditing(false);
    const trimmed = draft.trim();
    if (field && trimmed !== displayVal) onSave(field.id, trimmed);
  }, [draft, displayVal, field, onSave]);

  const cancel = useCallback(() => {
    setEditing(false);
    setDraft(displayVal);
  }, [displayVal]);

  const handleKeyDown = useCallback((e: React.KeyboardEvent) => {
    if (e.key === 'Enter') { e.preventDefault(); save(); }
    else if (e.key === 'Escape') { e.preventDefault(); cancel(); }
  }, [save, cancel]);

  const isLow = field && (field.confidence ?? 1) < 0.7;
  const alignClass = align === 'right' ? 'text-right' : align === 'center' ? 'text-center' : 'text-left';

  if (!field) {
    return <span className={`text-muted-foreground ${alignClass} ${className}`}>{placeholder}</span>;
  }

  if (editing) {
    return (
      <input
        ref={inputRef}
        type="text"
        value={draft}
        onChange={e => setDraft(e.target.value)}
        onBlur={save}
        onKeyDown={handleKeyDown}
        className={`border border-ring rounded px-1.5 py-0.5 text-sm w-full bg-background focus:outline-none focus:ring-1 focus:ring-ring ${alignClass}`}
      />
    );
  }

  return (
    <span
      onClick={() => { setDraft(displayVal); setEditing(true); }}
      title="Click to edit"
      className={`cursor-pointer rounded px-1 -mx-1 transition-colors hover:bg-primary/10 ${isLow ? 'bg-red-50' : ''} ${alignClass} ${className}`}
    >
      {field.isManuallyCorreected && (
        <Pencil className="inline h-2.5 w-2.5 text-primary mr-0.5 mb-0.5" />
      )}
      {displayVal || <span className="text-muted-foreground">{placeholder}</span>}
    </span>
  );
}

// ─── (SummaryCard, AgingBucket, DOC_TYPE_LABELS removed — generic config-driven view used instead) ──

// ─── Main Page ────────────────────────────────────────────────────────────────

export default function DocumentDetailPage() {
  const { id } = useParams<{ id: string }>();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const { isAdmin } = useAuth();
  const [showDeleteModal, setShowDeleteModal] = useState(false);
  const [editingType, setEditingType] = useState(false);
  const [summaryRefreshing, setSummaryRefreshing] = useState(false);
  const [pendingTypeId, setPendingTypeId] = useState<string>('');
  const [pendingIds, setPendingIds] = useState<Set<string>>(new Set());
  const [paddleRawRunning, setPaddleRawRunning] = useState(false);
  const [paddleRawText, setPaddleRawText] = useState<string | null>(null);

  // Tracks doc status across renders to detect the Processing → PendingReview transition.
  const prevStatusRef = useRef<string | undefined>(undefined);

  const { data: doc, isLoading } = useQuery<Document>({
    queryKey: ['document', id],
    queryFn: () => documentApi.getById(id!).then(r => r.data),
    enabled: !!id,
    // Poll while Uploaded (OCR may have been started from UploadPage) or Processing.
    refetchInterval: q => ['Uploaded', 'Processing'].includes(q.state.data?.status ?? '') ? 3000 : false,
  });

  const { data: ocrResult } = useQuery<OcrResult>({
    queryKey: ['ocr-result', id],
    queryFn: () => ocrApi.getResult(id!).then(r => r.data),
    // Disable while Processing so the cleared cache (set in onMutate) never triggers
    // a premature 404 fetch — the query auto-enables when status becomes PendingReview.
    enabled: !!id && doc?.status !== 'Uploaded' && doc?.status !== 'Processing',
    retry: false,
  });

  const { data: docTypes } = useQuery<DocumentType[]>({
    queryKey: ['document-types'],
    queryFn: () => configApi.getDocumentTypes().then(r => r.data),
  });

  const { data: fieldConfigs } = useQuery<FieldMappingConfig[]>({
    queryKey: ['field-mappings', doc?.documentTypeId],
    queryFn: () => configApi.getFieldMappings(doc!.documentTypeId!).then(r => r.data),
    enabled: !!doc?.documentTypeId,
  });

  const { data: validations } = useQuery<ValidationResult[]>({
    queryKey: ['validation', id],
    queryFn: () => validationApi.getResults(id!).then(r => r.data),
    enabled: !!id && doc?.status !== 'Uploaded',
    retry: false,
  });

  const invalidate = () => {
    queryClient.invalidateQueries({ queryKey: ['document', id] });
    queryClient.invalidateQueries({ queryKey: ['documents'] });
  };

  const triggerOcr = useMutation({
    mutationFn: () => ocrApi.process(id!),
    onMutate: () => {
      // Remove the ocr-result query entirely so stale field IDs don't linger.
      // setQueryData(null) leaves null in cache which can confuse fetchQuery later;
      // removeQueries ensures the next fetch is always a clean server round-trip.
      queryClient.removeQueries({ queryKey: ['ocr-result', id] });
      queryClient.setQueryData(['validation', id], []);
      // Optimistically set status to Processing so polling kicks in without a server round-trip.
      queryClient.setQueryData<Document>(['document', id], old =>
        old ? { ...old, status: 'Processing' } : old
      );
    },
    onSuccess: () => {
      // Only refresh the documents list — do NOT refetch the individual document here.
      // The server still has status='Uploaded' immediately after the 202 response (OCR runs
      // async), so refetching now would overwrite the optimistic 'Processing' state.
      // Polling (every 3 s while Processing/Uploaded) will pick up the real status change.
      queryClient.invalidateQueries({ queryKey: ['documents'] });
    },
  });

  const deleteDoc = useMutation({
    mutationFn: () => documentApi.delete(id!),
    onSuccess: () => navigate('/documents'),
  });

  const markChecked = useMutation({
    mutationFn: () => documentApi.updateStatus(id!, 'Checked'),
    onSuccess: () => invalidate(),
  });

  const assignType = useMutation({
    mutationFn: (typeId: string | null) => documentApi.assignDocumentType(id!, typeId),
    onSuccess: () => { invalidate(); setEditingType(false); },
  });

  // Tracks whether the sequential "Run Validation" loop is running (for button state).
  const [isRunning, setIsRunning] = useState(false);
  // Set to true when the user clicks Stop — checked at each loop iteration.
  const stopRequestedRef = useRef(false);

  const validateField = useMutation({
    mutationFn: (fieldId: string) =>
      validationApi.validateField(id!, fieldId).then(r => r.data as ValidationResult[]),
    onMutate: (fieldId) => {
      setPendingIds(prev => new Set([...prev, fieldId]));
    },
    onSettled: (_, __, fieldId) => {
      setPendingIds(prev => { const next = new Set(prev); next.delete(fieldId); return next; });
    },
    onSuccess: (newResults, fieldId) => {
      queryClient.setQueryData<ValidationResult[]>(['validation', id], old => {
        const base = old ?? [];
        return [...base.filter(v => v.extractedFieldId !== fieldId), ...newResults];
      });
    },
  });

  const correctField = useMutation({
    mutationFn: ({ fieldId, value }: { fieldId: string; value: string }) =>
      ocrApi.correctField(id!, fieldId, value),
    onSuccess: (_, { fieldId }) => {
      queryClient.invalidateQueries({ queryKey: ['ocr-result', id] });
      // Only re-validate if the field has ERP mapping — non-ERP fields have no validator to run.
      if (validatableFieldIds.includes(fieldId)) validateField.mutate(fieldId);
    },
  });

  const save = useCallback((fieldId: string, value: string) => {
    correctField.mutate({ fieldId, value });
  }, [correctField]);

  const allFieldIds = useMemo(() =>
    (ocrResult?.fields ?? []).map(f => f.id),
  [ocrResult?.fields]);

  const startEditType = () => { setPendingTypeId(doc?.documentTypeId ?? ''); setEditingType(true); };

  // ── Field config lookup + header/table split ────────────────────────────
  const fieldConfigMap = useMemo(() => {
    const map: Record<string, FieldMappingConfig> = {};
    for (const c of fieldConfigs ?? []) map[c.fieldName.toLowerCase()] = c;
    return map;
  }, [fieldConfigs]);

  // Only fields that have an ERP mapping key get a spinner — fields with no validation
  // configured don't need to show a checking state.
  const validatableFieldIds = useMemo(() =>
    (ocrResult?.fields ?? [])
      .filter(f => {
        const cfg = fieldConfigMap[f.fieldName.toLowerCase()];
        return cfg?.erpMappingKey && !cfg.isManualEntry;
      })
      .map(f => f.id),
  [ocrResult?.fields, fieldConfigMap]);

  // Groups header fields sequentially (so dependency failures can short-circuit dependents),
  // then table rows sequentially row-by-row with the same dependency skip logic.
  const runSequential = useCallback(async (fields: ExtractedField[]) => {
    const validatable = fields.filter(f => {
      const cfg = fieldConfigMap[f.fieldName.toLowerCase()];
      return cfg?.erpMappingKey && !cfg.isManualEntry && !cfg.isCheckbox;
    });

    const checkIsTableField = (name: string) => {
      const cfg = fieldConfigMap[name.toLowerCase()];
      if (cfg) return cfg.allowMultiple;
      return (fields.filter(f => f.fieldName === name).length) > 1;
    };

    const headerFieldsToRun = validatable
      .filter(f => !checkIsTableField(f.fieldName))
      .sort((a, b) =>
        (fieldConfigMap[a.fieldName.toLowerCase()]?.displayOrder ?? 999) -
        (fieldConfigMap[b.fieldName.toLowerCase()]?.displayOrder ?? 999));
    const tableFields = validatable.filter(f => checkIsTableField(f.fieldName));

    // Track which field NAMES failed — used to skip dependents.
    const failedFieldNames = new Set<string>();

    // 1. Run header fields one-by-one so we can skip dependents whose parent failed.
    for (const f of headerFieldsToRun) {
      if (stopRequestedRef.current) break;
      const cfg = fieldConfigMap[f.fieldName.toLowerCase()];
      // Skip if this field's dependency already failed.
      if (cfg?.dependentFieldKey && failedFieldNames.has(cfg.dependentFieldKey.toLowerCase())) continue;
      try {
        const results = await validateField.mutateAsync(f.id);
        if (results.some((r: ValidationResult) => r.status === 'Failed'))
          failedFieldNames.add(f.fieldName.toLowerCase());
      } catch { /* onError handles 424 */ }
    }

    // 2. Group table fields by column name and run row-by-row.
    const colNames = [...new Set(tableFields.map(f => f.fieldName))];
    const groupedCols: Record<string, ExtractedField[]> = {};
    for (const name of colNames) groupedCols[name] = fields.filter(f => f.fieldName === name);
    const maxRows = Math.max(...Object.values(groupedCols).map(g => g.length), 0);

    for (let i = 0; i < maxRows; i++) {
      if (stopRequestedRef.current) break;
      // Track IDs that failed within this row to skip intra-row dependents.
      const failedRowFieldNames = new Set<string>();

      // Sort columns by displayOrder so dependency order is respected within each row.
      const sortedCols = [...colNames].sort((a, b) =>
        (fieldConfigMap[a.toLowerCase()]?.displayOrder ?? 999) -
        (fieldConfigMap[b.toLowerCase()]?.displayOrder ?? 999));

      for (const name of sortedCols) {
        if (stopRequestedRef.current) break;
        const f = groupedCols[name]?.[i];
        if (!f || !fieldConfigMap[name.toLowerCase()]?.erpMappingKey) continue;
        const cfg = fieldConfigMap[name.toLowerCase()];
        const depKey = cfg?.dependentFieldKey?.toLowerCase();
        // Skip if global dependency (e.g. vendorName) or row-level dependency failed.
        if (depKey && (failedFieldNames.has(depKey) || failedRowFieldNames.has(depKey))) continue;
        try {
          const results = await validateField.mutateAsync(f.id);
          if (results.some((r: ValidationResult) => r.status === 'Failed'))
            failedRowFieldNames.add(name.toLowerCase());
        } catch { /* onError handles 424 */ }
      }
    }
  }, [fieldConfigMap, validateField, stopRequestedRef]);

  const runAllValidations = useCallback(async () => {
    stopRequestedRef.current = false;
    setIsRunning(true);
    await runSequential(ocrResult?.fields ?? []);
    // Only clear isRunning if the user hasn't already stopped it — prevents a
    // re-render flash where the Stop button briefly reappears as Run Validation.
    if (!stopRequestedRef.current) setIsRunning(false);
  }, [runSequential, ocrResult?.fields]);

  const stopValidation = useCallback(() => {
    stopRequestedRef.current = true;
    setIsRunning(false);
    setPendingIds(new Set());
  }, []);

  const isValidating = isRunning || allFieldIds.some(id => pendingIds.has(id));

  // Auto-run validation when OCR completes (Processing → PendingReview).
  // setIsRunning(true) fires BEFORE the async fetch so the UI enters validating mode immediately.
  // fetchQuery explicitly fetches fresh OCR fields — avoids relying on stale cache or
  // reference-equality tricks that can cause Phase 2 effects to silently skip.
  useEffect(() => {
    if (['Uploaded', 'Processing'].includes(prevStatusRef.current ?? '') && doc?.status === 'PendingReview') {
      queryClient.setQueryData(['validation', id], []);
      stopRequestedRef.current = false;
      setIsRunning(true);
      ocrApi.getResult(id!).then(r => r.data)
        .then((freshResult: OcrResult) => {
          // Explicitly push the fresh OCR result into the cache so the useQuery observer
          // (enabled now that status is PendingReview) definitely renders the new fields.
          queryClient.setQueryData(['ocr-result', id], freshResult);
          const fields = freshResult?.fields ?? [];
          if (!stopRequestedRef.current) return runSequential(fields);
        })
        .catch(() => { /* OCR result not ready yet — polling will retry */ })
        .finally(() => { if (!stopRequestedRef.current) setIsRunning(false); });
    }
    prevStatusRef.current = doc?.status;
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [doc?.status]);

  const refreshSummary = useCallback(async () => {
    setSummaryRefreshing(true);
    await Promise.all([
      queryClient.invalidateQueries({ queryKey: ['ocr-result', id] }),
      queryClient.invalidateQueries({ queryKey: ['validation', id] }),
    ]);
    setSummaryRefreshing(false);
  }, [queryClient, id]);

  const isTableField = useCallback((fieldName: string) => {
    const cfg = fieldConfigMap[fieldName.toLowerCase()];
    if (cfg) return cfg.allowMultiple;
    // Unconfigured extra: treat as table if it appears more than once
    return (ocrResult?.fields.filter(f => f.fieldName === fieldName).length ?? 0) > 1;
  }, [fieldConfigMap, ocrResult]);

  // Header fields: single-value, first occurrence of each name, sorted by displayOrder
  const headerFields = useMemo(() => {
    const fields = ocrResult?.fields.filter((f, i, arr) =>
      !isTableField(f.fieldName) &&
      arr.findIndex(x => x.fieldName === f.fieldName) === i
    ) ?? [];
    return fields.sort((a, b) =>
      (fieldConfigMap[a.fieldName.toLowerCase()]?.displayOrder ?? 999) -
      (fieldConfigMap[b.fieldName.toLowerCase()]?.displayOrder ?? 999)
    );
  }, [ocrResult, isTableField, fieldConfigMap]);

  // Table fields: grouped by fieldName, sorted by displayOrder
  const tableColumnNames = useMemo(() => {
    const names = [...new Set(ocrResult?.fields
      .filter(f => isTableField(f.fieldName))
      .map(f => f.fieldName) ?? []
    )];
    return names.sort((a, b) =>
      (fieldConfigMap[a.toLowerCase()]?.displayOrder ?? 999) -
      (fieldConfigMap[b.toLowerCase()]?.displayOrder ?? 999)
    );
  }, [ocrResult, isTableField, fieldConfigMap]);

  const tableFieldGroups = useMemo(() => {
    const groups: Record<string, ExtractedField[]> = {};
    for (const name of tableColumnNames)
      groups[name] = ocrResult?.fields.filter(f => f.fieldName === name) ?? [];
    return groups;
  }, [tableColumnNames, ocrResult]);

  const tableRowCount = useMemo(() =>
    Math.max(...Object.values(tableFieldGroups).map(g => g.length), 0),
  [tableFieldGroups]);

  // Which table-row field IDs belong to "settled" rows (any isCheckbox column is "true").
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

  if (isLoading) return <div className="text-center py-12 text-muted-foreground">Loading...</div>;
  if (!doc) return <div className="text-center py-12 text-destructive">Document not found.</div>;

  // Include Processing so users can force-restart a stuck OCR (e.g. previous run timed out).
  const canOcr  = ['Uploaded', 'PendingReview', 'ReviewInProgress', 'Processing'].includes(doc.status);
  const isRerun = doc.status !== 'Uploaded';

  const allValidations = validations ?? [];
  const validPassed   = allValidations.filter(v => v.status === 'Passed').length;
  const validFailed   = allValidations.filter(v => v.status === 'Failed').length;
  const validWarnings = allValidations.filter(v => v.status === 'Warning').length;

  const docType = docTypes?.find(dt => dt.id === doc.documentTypeId);
  const isVendorStatement = docType?.category === 'VendorStatement';

  // ── Vendor Statement summary values ─────────────────────────────────────────
  const stmtFieldVal = (name: string): string | undefined => {
    const f = ocrResult?.fields.find(f => f.fieldName.toLowerCase() === name.toLowerCase());
    if (!f) return undefined;
    return (f.isManuallyCorreected && f.correctedValue) ? f.correctedValue : (f.normalizedValue ?? f.rawValue ?? undefined);
  };
  const stmtParseAmt = (v: string | undefined): number => {
    if (!v) return NaN;
    const n = parseFloat(v.replace(/[^\d.\-]/g, ''));
    return isNaN(n) ? NaN : n;
  };
  const stmtFmt = (n: number) =>
    isNaN(n) ? '—' : n.toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 });

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

  const getValidationStatus = (fieldId: string) => {
    const vs = allValidations.filter(v => v.extractedFieldId === fieldId);
    if (vs.some(v => v.status === 'Failed'))  return 'Failed';
    if (vs.some(v => v.status === 'Warning')) return 'Warning';
    if (vs.some(v => v.status === 'Passed'))  return 'Passed';
    return null;
  };
  const getValidationMsgs = (fieldId: string) =>
    allValidations.filter(v => v.extractedFieldId === fieldId && v.message);

  return (
    <div className="space-y-5 max-w-5xl mx-auto">

      {/* ── Header ─────────────────────────────────────────────────────── */}
      <div className="flex items-center gap-2 sm:gap-3">
        <button onClick={() => navigate('/documents')} className="text-muted-foreground hover:text-foreground flex-shrink-0">
          <ChevronLeft className="h-5 w-5" />
        </button>
        <div className="flex-1 min-w-0">
          <h1 className="text-base sm:text-xl font-bold text-foreground truncate">{doc.originalFilename}</h1>
          <p className="text-sm text-muted-foreground">{doc.documentTypeName ?? 'No type assigned'}</p>
        </div>
        <StatusBadge status={doc.status} />
      </div>

      {/* ── Action Bar ─────────────────────────────────────────────────── */}
      <div className="card p-4 flex flex-wrap gap-2">
        {canOcr && (
          <button
            onClick={() => triggerOcr.mutate()}
            disabled={triggerOcr.isPending}
            className="btn-primary flex items-center gap-2 text-sm"
          >
            {triggerOcr.isPending ? <Loader2 className="h-4 w-4 animate-spin" /> : <Cpu className="h-4 w-4" />}
            {isRerun ? 'Re-run OCR' : 'Run OCR'}
          </button>
        )}

        {doc.status !== 'Uploaded' && doc.status !== 'Processing' && (
          isValidating ? (
            <button
              onClick={stopValidation}
              className="btn-secondary flex items-center gap-2 text-sm text-destructive border-destructive/40 hover:bg-destructive/10"
            >
              <StopCircle className="h-4 w-4" />
              Stop Validation
            </button>
          ) : (
            <button
              onClick={runAllValidations}
              className="btn-secondary flex items-center gap-2 text-sm"
            >
              <RefreshCw className="h-4 w-4" />
              Run Validation
            </button>
          )
        )}

        {doc.status !== 'Uploaded' && doc.status !== 'Processing' && (
          <Link to={`/documents/${id}/verify`} className="btn-secondary flex items-center gap-2 text-sm">
            <FileText className="h-4 w-4" /> Review Fields
          </Link>
        )}

        {['PendingReview', 'ReviewInProgress'].includes(doc.status) && (
          <button
            onClick={() => markChecked.mutate()}
            disabled={markChecked.isPending}
            className="btn-secondary flex items-center gap-2 text-sm text-teal-700 border-teal-300 hover:bg-teal-50"
          >
            {markChecked.isPending
              ? <Loader2 className="h-4 w-4 animate-spin" />
              : <CheckSquare className="h-4 w-4" />}
            Mark as Checked
          </button>
        )}

        {isAdmin && (
          <button
            onClick={() => setShowDeleteModal(true)}
            className="btn-secondary flex items-center gap-2 text-sm text-destructive border-red-200 hover:bg-red-50 ml-auto"
          >
            <XCircle className="h-4 w-4" /> Delete
          </button>
        )}
      </div>

      {triggerOcr.isError && (
        <div className="bg-red-50 border border-red-200 rounded-lg p-4 text-sm text-red-700">
          OCR processing failed. Check that the document is a valid PDF, PNG, or TIFF and try again.
        </div>
      )}

      {/* ── Document Info + OCR Summary ────────────────────────────────── */}
      <div className="grid md:grid-cols-2 gap-5">
        <div className="card p-5 space-y-3">
          <h2 className="font-semibold text-foreground flex items-center gap-2">
            <FileText className="h-4 w-4" /> Document Info
          </h2>
          <dl className="space-y-2 text-sm">
            <Row label="Filename" value={doc.originalFilename} />
            <div className="flex justify-between gap-4 items-center">
              <dt className="text-muted-foreground flex-shrink-0">Type</dt>
              {editingType ? (
                <div className="flex items-center gap-1.5">
                  <select
                    className="input text-sm py-0.5 h-7"
                    value={pendingTypeId}
                    onChange={e => setPendingTypeId(e.target.value)}
                  >
                    <option value="">— None —</option>
                    {docTypes?.map(dt => (
                      <option key={dt.id} value={dt.id}>{dt.displayName}</option>
                    ))}
                  </select>
                  <button
                    onClick={() => assignType.mutate(pendingTypeId || null)}
                    disabled={assignType.isPending}
                    className="text-green-600 hover:text-green-700 disabled:opacity-50"
                  >
                    {assignType.isPending ? <Loader2 className="h-4 w-4 animate-spin" /> : <Check className="h-4 w-4" />}
                  </button>
                  <button onClick={() => setEditingType(false)} className="text-muted-foreground hover:text-foreground">
                    <X className="h-4 w-4" />
                  </button>
                </div>
              ) : (
                <dd className="text-foreground text-right flex items-center gap-1.5">
                  {doc.documentTypeName ?? <span className="text-muted-foreground">None</span>}
                  <button onClick={startEditType} className="text-muted-foreground hover:text-foreground">
                    <Edit2 className="h-3.5 w-3.5" />
                  </button>
                </dd>
              )}
            </div>
            <Row label="Status" value={<StatusBadge status={doc.status} />} />
            <Row label="Uploaded by" value={doc.uploadedByUsername} />
            <Row label="Uploaded at" value={new Date(doc.uploadedAt).toLocaleString()} />
            {doc.processedAt && <Row label="Processed at" value={new Date(doc.processedAt).toLocaleString()} />}
            {doc.reviewedByUsername && <Row label="Reviewed by" value={doc.reviewedByUsername} />}
            {doc.approvedByUsername && <Row label="Approved by" value={doc.approvedByUsername} />}
          </dl>
        </div>

        <div className="card p-5 space-y-3">
          <h2 className="font-semibold text-foreground flex items-center gap-2">
            <Cpu className="h-4 w-4" /> OCR Summary
          </h2>
          {!ocrResult ? (
            <p className="text-sm text-muted-foreground">
              {doc.status === 'Uploaded'
                ? 'OCR has not been run yet.'
                : doc.status === 'Processing'
                  ? <span className="flex items-center gap-2"><Clock className="h-4 w-4 animate-spin" /> Processing...</span>
                  : 'No OCR result available.'}
            </p>
          ) : (
            <>
              <div className="flex flex-wrap gap-2">
                <span className={`text-xs px-2 py-0.5 rounded-full font-medium ${
                  (ocrResult.overallConfidence ?? 0) >= 0.9 ? 'bg-green-100 text-green-800'
                  : (ocrResult.overallConfidence ?? 0) >= 0.7 ? 'bg-amber-100 text-amber-800'
                  : 'bg-red-100 text-red-800'
                }`}>
                  {((ocrResult.overallConfidence ?? 0) * 100).toFixed(1)}% confidence
                </span>
                {ocrResult.engineVersion && (
                  <span className="text-xs px-2 py-0.5 rounded-full bg-muted text-muted-foreground">
                    {ocrResult.engineVersion}
                  </span>
                )}
              </div>
              <dl className="space-y-2 text-sm">
                <Row label="Pages" value={String(ocrResult.pageCount)} />
                <Row label="Fields extracted" value={String(ocrResult.fields.length)} />
                <Row label="Processing time" value={`${ocrResult.processingMs} ms`} />
                {allValidations.length > 0 && (
                  <div className="pt-2 mt-2 border-t border-border flex flex-wrap gap-3">
                    {validPassed > 0 && (
                      <span className="text-xs font-medium text-green-600 flex items-center gap-1">
                        <CheckCircle className="h-3.5 w-3.5" /> {validPassed} found
                      </span>
                    )}
                    {validWarnings > 0 && (
                      <span className="text-xs font-medium text-amber-600 flex items-center gap-1">
                        <AlertTriangle className="h-3.5 w-3.5" /> {validWarnings} warnings
                      </span>
                    )}
                    {validFailed > 0 && (
                      <span className="text-xs font-medium text-red-600 flex items-center gap-1">
                        <XCircle className="h-3.5 w-3.5" /> {validFailed} not found
                      </span>
                    )}
                    {isValidating && (
                      <span className="text-xs text-muted-foreground flex items-center gap-1">
                        <Loader2 className="h-3.5 w-3.5 animate-spin" /> Validating…
                      </span>
                    )}
                  </div>
                )}
              </dl>
            </>
          )}
          {ocrResult && !doc.documentTypeId && (
            <p className="text-xs text-amber-600 bg-amber-50 border border-amber-200 rounded p-2">
              No document type assigned — assign a type above and re-run OCR to extract fields.
            </p>
          )}
        </div>
      </div>

      {/* ── Vendor Statement Summary ──────────────────────────────────── */}
      {ocrResult && isVendorStatement && (() => {
        const openingBalance  = stmtFieldVal('openingBalance');
        const outstandingStmt = stmtFieldVal('outstandingBalance');
        const statementDate   = stmtFieldVal('statementDate');

        // Parse an amount string, treating a trailing "CR" as a negative sign.
        const parseAmt = (v: string): number => {
          const isCR = /\s*cr\s*$/i.test(v.trim());
          const n = parseFloat(v.replace(/[^\d.\-]/g, ''));
          return isNaN(n) ? NaN : (isCR ? -Math.abs(n) : n);
        };

        // Mutually exclusive: credit/payment columns must NOT be counted as debit.
        const debitColNames  = tableColumnNames.filter(n => /amount|debit/i.test(n) && !/credit|payment/i.test(n));
        const creditColNames = tableColumnNames.filter(n => /credit|payment/i.test(n));

        // Detect B/F (brought-forward / opening balance) rows by checking the ref column.
        // These rows carry the opening balance carried over — they must NOT be counted as
        // invoice debits, otherwise the opening balance would be double-counted.
        const refColName = tableColumnNames.find(n =>
          /ref|invoice.*no|inv.*no/i.test(n) &&
          !/amount|balance|debit|credit|date|due/i.test(n)
        );
        const isBFRow = (i: number): boolean => {
          if (!refColName) return false;
          const f = tableFieldGroups[refColName]?.[i];
          if (!f) return false;
          const v = (f.isManuallyCorreected && f.correctedValue)
            ? f.correctedValue : (f.normalizedValue ?? f.rawValue ?? '');
          return /^b[\/\-]?f$|^balance\s*b[\/\-]?f$|^brought\s*forward$|^opening/i.test(v.trim());
        };

        // Sum debit columns: only positive amounts, excluding B/F rows.
        // Negative amounts (or "CR"-suffixed) in a debit column are credits — add to credit.
        let totalDebit  = 0;
        let totalCredit = 0;
        for (const colName of debitColNames) {
          (tableFieldGroups[colName] ?? []).forEach((f, i) => {
            if (isBFRow(i)) return;
            const v = (f.isManuallyCorreected && f.correctedValue)
              ? f.correctedValue : (f.normalizedValue ?? f.rawValue ?? '');
            const n = parseAmt(v);
            if (isNaN(n)) return;
            if (n > 0) totalDebit  += n;
            else       totalCredit += Math.abs(n);  // CR/negative in debit column = credit
          });
        }
        for (const colName of creditColNames) {
          // Dedicated credit column — sum absolute values (may already be positive)
          (tableFieldGroups[colName] ?? []).forEach((f, i) => {
            if (isBFRow(i)) return;
            const v = (f.isManuallyCorreected && f.correctedValue)
              ? f.correctedValue : (f.normalizedValue ?? f.rawValue ?? '');
            const n = parseAmt(v);
            if (!isNaN(n) && n !== 0) totalCredit += Math.abs(n);
          });
        }

        // Total Invoice Amount (System): per debit-column field —
        //   Passed        → statement amount is correct (matches ERP)
        //   Failed/Warning → use ERP amount (statement is wrong; take system as authoritative)
        //   No validation → use statement amount as provisional
        let totalInvoiceSystem = 0;
        let matchedLineCount   = 0;
        let unmatchedLineCount = 0;

        for (const colName of debitColNames) {
          const col = tableFieldGroups[colName] ?? [];
          col.forEach((f, i) => {
            if (isBFRow(i)) return; // exclude B/F from invoice total too
            const fVals = allValidations.filter(v => v.extractedFieldId === f.id);
            const rawVal = (f.isManuallyCorreected && f.correctedValue)
              ? f.correctedValue : (f.normalizedValue ?? f.rawValue ?? '');
            const stmtNum = parseAmt(rawVal);
            const passed = fVals.find(v => v.status === 'Passed');
            const failed = fVals.find(v => v.status === 'Failed' || v.status === 'Warning');
            if (passed) {
              totalInvoiceSystem += isNaN(stmtNum) ? 0 : stmtNum;
              matchedLineCount++;
            } else if (failed) {
              const cfg    = fieldConfigMap[colName.toLowerCase()];
              const erpKey = cfg?.erpResponseField ?? 'Amount';
              const erpAmt = getErpValue(failed.erpReference, erpKey);
              const erpNum = erpAmt ? parseFloat(erpAmt) : NaN;
              totalInvoiceSystem += !isNaN(erpNum) ? erpNum : (!isNaN(stmtNum) ? stmtNum : 0);
              unmatchedLineCount++;
            } else {
              totalInvoiceSystem += isNaN(stmtNum) ? 0 : stmtNum;
            }
          });
        }

        // Use tableRowCount (max rows across ALL columns) as the line-count denominator
        // so it matches the number of rows the user sees in the Table Data section.
        const displayTotalLines = tableRowCount;

        // ERP outstanding balance from VendorStatement validation
        const outstandingValidation = allValidations.find(v =>
          v.fieldName?.toLowerCase() === 'outstandingbalance' &&
          v.validationType === 'VendorStatement' &&
          v.status !== 'Skipped'
        );
        const erpComputed  = outstandingValidation
          ? getErpValue(outstandingValidation.erpReference, 'computed') : undefined;
        const openBillCount = outstandingValidation
          ? parseInt(getErpValue(outstandingValidation.erpReference, 'billCount') ?? '0', 10) : 0;

        // VendorId from vendorName validation
        const vendorNameVal = allValidations.find(v =>
          v.fieldName?.toLowerCase() === 'vendorname' && v.status === 'Passed'
        );
        const vendorId = vendorNameVal
          ? getErpValue(vendorNameVal.erpReference, 'vendorId') : undefined;

        const outstandingStmtNum = stmtParseAmt(outstandingStmt);
        const erpComputedNum     = erpComputed ? parseFloat(erpComputed) : NaN;
        const balanceDiff        = (!isNaN(outstandingStmtNum) && !isNaN(erpComputedNum))
          ? Math.abs(outstandingStmtNum - erpComputedNum) : null;
        const isBalanceMatch = balanceDiff !== null && balanceDiff <= Math.max(erpComputedNum * 0.01, 1);

        // Invoice variance: statement total debit vs system computed total
        const invoiceVariance = displayTotalLines > 0 ? totalDebit - totalInvoiceSystem : NaN;
        const isInvoiceMatch  = !isNaN(invoiceVariance) && Math.abs(invoiceVariance) <= 0.01;

        // Internal formula check: Outstanding = Opening + Debit − Credit
        const openingBalanceNum    = stmtParseAmt(openingBalance);
        const formulaOutstanding   = !isNaN(openingBalanceNum) && (totalDebit > 0 || totalCredit > 0)
          ? openingBalanceNum + totalDebit - totalCredit : NaN;
        const formulaDiff = !isNaN(formulaOutstanding) && !isNaN(outstandingStmtNum)
          ? Math.abs(formulaOutstanding - outstandingStmtNum) : null;
        const isFormulaMatch = formulaDiff !== null && formulaDiff <= 0.01;

        type Highlight = 'pass' | 'fail' | 'warn' | null;
        const balHighlight: Highlight = balanceDiff !== null ? (isBalanceMatch ? 'pass' : 'fail') : null;
        // For the outstanding cell highlight: formula takes priority when ERP data absent
        const outstandingHighlight: Highlight = balHighlight
          ?? (formulaDiff !== null ? (isFormulaMatch ? 'pass' : 'fail') : null);
        const invHighlight: Highlight = displayTotalLines === 0 ? null
          : isInvoiceMatch ? 'pass'
          : unmatchedLineCount > 0 ? 'fail'
          : null;

        const cellCls = (h: Highlight) =>
          h === 'pass' ? 'border-green-300 bg-green-50/40'
          : h === 'fail' ? 'border-red-300 bg-red-50/40'
          : h === 'warn' ? 'border-amber-300 bg-amber-50/40'
          : 'border-border bg-muted/20';

        const Cell = ({
          label, value, sub, highlight, dim,
        }: {
          label: string; value: string; sub?: string; highlight?: Highlight; dim?: boolean;
        }) => (
          <div className={`rounded-lg border px-3 py-2.5 space-y-0.5 ${cellCls(highlight ?? null)}`}>
            <p className={`text-[11px] font-semibold uppercase tracking-wide ${dim ? 'text-muted-foreground/60' : 'text-muted-foreground'}`}>{label}</p>
            <p className={`text-sm font-medium ${dim ? 'text-muted-foreground' : 'text-foreground'}`}>{value}</p>
            {sub && <p className="text-[10px] text-muted-foreground leading-tight">{sub}</p>}
          </div>
        );

        return (
          <div className="card p-5 space-y-4">
            {/* Header */}
            <div className="flex items-center justify-between">
              <h2 className="font-semibold text-foreground flex items-center gap-2">
                <BarChart2 className="h-4 w-4" /> Statement Summary
              </h2>
              <button
                onClick={refreshSummary}
                disabled={summaryRefreshing}
                title="Refresh summary amounts"
                className="btn-secondary flex items-center gap-1.5 text-xs py-1 px-2.5"
              >
                <RefreshCw className={`h-3.5 w-3.5 ${summaryRefreshing ? 'animate-spin' : ''}`} />
                {summaryRefreshing ? 'Refreshing…' : 'Refresh'}
              </button>
            </div>

            {/* Row 1 — From Statement */}
            <div>
              <p className="text-[10px] font-semibold uppercase tracking-widest text-muted-foreground/70 mb-2">From Statement</p>
              <div className="grid grid-cols-2 sm:grid-cols-4 gap-3">
                <Cell
                  label="Opening Balance"
                  value={openingBalance ? stmtFmt(openingBalanceNum) : '—'}
                  sub={statementDate ?? undefined}
                />
                <Cell
                  label="Total Debit"
                  value={totalDebit > 0 ? stmtFmt(totalDebit) : '—'}
                  sub={displayTotalLines > 0 ? `${displayTotalLines} line${displayTotalLines !== 1 ? 's' : ''}` : undefined}
                />
                <Cell
                  label="Total Credit"
                  value={totalCredit > 0 ? stmtFmt(totalCredit) : '—'}
                />
                <Cell
                  label="Outstanding Balance"
                  value={outstandingStmt ? stmtFmt(outstandingStmtNum) : '—'}
                  highlight={outstandingHighlight}
                  sub={formulaDiff !== null
                    ? (isFormulaMatch
                        ? `✓ Opening + Debit − Credit`
                        : `⚠ Expected: ${stmtFmt(formulaOutstanding)}`)
                    : undefined}
                />
              </div>
            </div>

            {/* Divider */}
            <div className="border-t border-dashed border-border" />

            {/* Row 2 — From Acumatica */}
            <div>
              <p className="text-[10px] font-semibold uppercase tracking-widest text-muted-foreground/70 mb-2">From Acumatica</p>
              <div className="grid grid-cols-2 sm:grid-cols-4 gap-3">
                <Cell
                  label="Verified Lines"
                  value={displayTotalLines > 0 ? `${matchedLineCount} / ${displayTotalLines}` : '—'}
                  sub={openBillCount > 0 ? `${openBillCount} open bill${openBillCount !== 1 ? 's' : ''} in ERP` : undefined}
                  highlight={displayTotalLines > 0
                    ? (unmatchedLineCount === 0 && matchedLineCount > 0 ? 'pass' : unmatchedLineCount > 0 ? 'fail' : null)
                    : null}
                  dim={displayTotalLines === 0}
                />
                <Cell
                  label="Total Invoice Amt"
                  value={displayTotalLines > 0 ? stmtFmt(totalInvoiceSystem) : '—'}
                  sub={unmatchedLineCount > 0 ? `${unmatchedLineCount} line${unmatchedLineCount !== 1 ? 's' : ''} adjusted to ERP` : undefined}
                  highlight={invHighlight}
                />
                <Cell
                  label="Invoice Variance"
                  value={!isNaN(invoiceVariance) && displayTotalLines > 0
                    ? (isInvoiceMatch ? '✓ Matched' : (invoiceVariance > 0 ? '+' : '') + stmtFmt(invoiceVariance))
                    : '—'}
                  sub={!isNaN(invoiceVariance) && !isInvoiceMatch && displayTotalLines > 0
                    ? 'Statement vs Acumatica' : undefined}
                  highlight={!isNaN(invoiceVariance) && displayTotalLines > 0
                    ? (isInvoiceMatch ? 'pass' : 'fail') : null}
                />
                <Cell
                  label="Outstanding Balance"
                  value={!isNaN(erpComputedNum) ? stmtFmt(erpComputedNum) : '—'}
                  sub={vendorId ? `Vendor: ${vendorId}` : undefined}
                  highlight={balHighlight}
                />
              </div>
            </div>

            {/* Reconciliation status banner */}
            {(balanceDiff !== null || !isNaN(invoiceVariance) || formulaDiff !== null) && (
              <div className={`flex items-start gap-2 text-sm rounded-lg p-3 ${
                (balanceDiff === null || isBalanceMatch) &&
                (isNaN(invoiceVariance) || isInvoiceMatch) &&
                (formulaDiff === null || isFormulaMatch)
                  ? 'text-green-700 bg-green-50 border border-green-200'
                  : 'text-red-700 bg-red-50 border border-red-200'
              }`}>
                {(balanceDiff === null || isBalanceMatch) &&
                 (isNaN(invoiceVariance) || isInvoiceMatch) &&
                 (formulaDiff === null || isFormulaMatch)
                  ? <CheckCircle className="h-4 w-4 flex-shrink-0 mt-0.5" />
                  : <AlertTriangle className="h-4 w-4 flex-shrink-0 mt-0.5" />}
                <span className="space-y-0.5">
                  {(balanceDiff === null || isBalanceMatch) &&
                   (isNaN(invoiceVariance) || isInvoiceMatch) &&
                   (formulaDiff === null || isFormulaMatch)
                    ? <>Statement reconciles — outstanding balance <strong>{stmtFmt(erpComputedNum)}</strong> and all invoice lines match Acumatica.</>
                    : <>
                        {formulaDiff !== null && !isFormulaMatch && (
                          <span className="block">Statement formula error: Opening ({stmtFmt(openingBalanceNum)}) + Debit ({stmtFmt(totalDebit)}) − Credit ({stmtFmt(totalCredit)}) = <strong>{stmtFmt(formulaOutstanding)}</strong>, but Outstanding shows <strong>{stmtFmt(outstandingStmtNum)}</strong> (diff: {stmtFmt(formulaDiff)}).</span>
                        )}
                        {!isBalanceMatch && balanceDiff !== null && (
                          <span className="block">Outstanding balance mismatch: statement <strong>{stmtFmt(outstandingStmtNum)}</strong> vs system <strong>{stmtFmt(erpComputedNum)}</strong> (diff: {stmtFmt(balanceDiff)}).</span>
                        )}
                        {!isInvoiceMatch && !isNaN(invoiceVariance) && (
                          <span className="block">Invoice variance of <strong>{stmtFmt(Math.abs(invoiceVariance))}</strong> between statement total debit and Acumatica{unmatchedLineCount > 0 ? ` (${unmatchedLineCount} line${unmatchedLineCount !== 1 ? 's' : ''} adjusted)` : ''}.</span>
                        )}
                      </>
                  }
                </span>
              </div>
            )}
          </div>
        );
      })()}

      {/* ── Header Fields (single-value) — grid card layout ─────────── */}
      {ocrResult && headerFields.length > 0 && (
        <div className={`card p-5 relative overflow-hidden transition-all ${isRunning ? 'bg-muted/30' : ''}`}>
          {isRunning && (
            <div className="absolute inset-x-0 top-0 h-1 bg-primary/20">
              <div className="h-full bg-primary animate-pulse w-full" />
            </div>
          )}
          <div className="flex items-center justify-between mb-4">
            <h2 className="font-semibold text-foreground flex items-center gap-2">
              Extracted Fields
              {isRunning && headerFields.some(f => pendingIds.has(f.id)) && (
                <span className="text-xs font-medium text-primary bg-primary/10 px-2 py-0.5 rounded-full animate-pulse flex items-center gap-1.5">
                  <Loader2 className="h-3 w-3 animate-spin" /> Validating...
                </span>
              )}
            </h2>
            <p className="text-xs text-muted-foreground flex items-center gap-1">
              <Pencil className="h-3 w-3" /> Click any value to edit
            </p>
          </div>
          {(() => {
            // Only treat displayOrder as row*10+col encoding when col part (d%10) >= 1.
            // displayOrder values like 10, 20, 30 are sequential ordering — not grid coords.
            const isExplicitFn = (d: number) => d >= 11 && (d % 10) >= 1;
            const hasExplicit = headerFields.some(f =>
              isExplicitFn(fieldConfigMap[f.fieldName.toLowerCase()]?.displayOrder ?? 0));

            // For explicit-placement types: fixed-column grid using max col index.
            // For auto-flow types: repeat(auto-fit) so items in every row — including
            // the last — stretch to fill the full row width equally.
            const maxCol = hasExplicit
              ? Math.max(
                  ...headerFields.map(f => {
                    const d = fieldConfigMap[f.fieldName.toLowerCase()]?.displayOrder ?? 0;
                    return isExplicitFn(d) ? d % 10 : 0;
                  }),
                  2)
              : null;
            return (
          <dl
            className="gap-3"
            style={{
              display: 'grid',
              gridTemplateColumns: maxCol
                ? `repeat(${maxCol}, minmax(0, 1fr))`
                : 'repeat(auto-fit, minmax(160px, 1fr))',
            }}
          >
            {headerFields.map(field => {
              const cfg = fieldConfigMap[field.fieldName.toLowerCase()];
              const fieldLabel = cfg?.displayLabel ?? field.fieldName;
              const isManual = cfg?.isManualEntry ?? false;
              const isLow = !isManual && (field.confidence ?? 1) < 0.7;
              const status = getValidationStatus(field.id);
              const isPending = pendingIds.has(field.id);
              const msgs = getValidationMsgs(field.id);
              const d = cfg?.displayOrder ?? 0;
              const explicit   = isExplicitFn(d);
              const gridRow    = explicit ? Math.floor(d / 10) : undefined;
              const gridColumn = explicit ? d % 10 : undefined;
              return (
                <div
                  key={field.id}
                  style={gridRow !== undefined ? { gridRow, gridColumn } : undefined}
                  className={`rounded-lg border px-3 py-2.5 space-y-1 transition-colors ${
                    isManual            ? 'border-violet-200 bg-violet-50/50'
                    : isPending         ? 'border-blue-200 bg-blue-50/30'
                    : status === 'Failed'  ? 'border-red-300 bg-red-50/60'
                    : status === 'Warning' ? 'border-amber-300 bg-amber-50/50'
                    : status === 'Passed'  ? 'border-green-300 bg-green-50/40'
                    : isLow             ? 'border-red-200 bg-red-50/20'
                    : 'border-border bg-muted/20'
                  }`}
                >
                  {/* Label row */}
                  <dt className="flex items-center gap-1 text-[11px] font-semibold uppercase tracking-wide text-muted-foreground">
                    {!isManual && isPending && <Loader2 className="h-3 w-3 text-blue-500 animate-spin flex-shrink-0" />}
                    {!isManual && !isPending && status && (
                      <button
                        onClick={e => { e.stopPropagation(); validateField.mutate(field.id); }}
                        title="Re-validate"
                        className="group flex-shrink-0 rounded focus:outline-none"
                      >
                        <span className="group-hover:hidden block">
                          {status === 'Failed'  && <XCircle      className="h-3 w-3 text-red-500" />}
                          {status === 'Warning' && <AlertTriangle className="h-3 w-3 text-amber-500" />}
                          {status === 'Passed'  && <CheckCircle   className="h-3 w-3 text-green-500" />}
                        </span>
                        <RefreshCw className="h-3 w-3 text-primary hidden group-hover:block" />
                      </button>
                    )}
                    <span className="truncate">{fieldLabel}</span>
                    {isManual && <span className="ml-auto text-[9px] px-1 py-0.5 rounded bg-violet-100 text-violet-700 font-medium">Manual</span>}
                  </dt>
                  {/* Value */}
                  <dd className="text-sm font-medium text-foreground">
                    <EditableCell field={field} onSave={save} placeholder="—" />
                  </dd>
                  {/* Confidence + validation messages */}
                  <div className="flex flex-wrap items-center gap-1 pt-0.5">
                    {!isManual && (
                      <span className={`text-[10px] px-1.5 py-0.5 rounded-full font-medium ${
                        isLow ? 'bg-red-100 text-red-700'
                        : (field.confidence ?? 0) >= 0.9 ? 'bg-green-100 text-green-700'
                        : 'bg-amber-100 text-amber-700'
                      }`}>
                        {Math.round((field.confidence ?? 0) * 100)}%
                      </span>
                    )}
                    {msgs.filter(v => v.status === status).map(v => {
                      const erpPassValue = v.status === 'Passed' && v.erpResponseField
                        ? getErpValue(v.erpReference, v.erpResponseField) : undefined;
                      const msgLabel = v.status === 'Passed'
                        ? (erpPassValue ? `✓ ${v.erpResponseField}: ${erpPassValue}` : '✓ In ERP')
                        : v.status === 'Warning' ? '⚠ Review'
                        : '✗ Not found';
                      return (
                        <span key={v.id} className={`text-[10px] px-1.5 py-0.5 rounded font-medium font-mono ${
                          v.status === 'Passed' ? 'bg-green-100 text-green-700'
                          : v.status === 'Warning' ? 'bg-amber-100 text-amber-700'
                          : 'bg-red-100 text-red-700'
                        }`} title={v.message ?? undefined}>
                          {msgLabel}
                        </span>
                      );
                    })}
                  </div>
                  {isManual && !field.correctedValue && !field.normalizedValue && !field.rawValue && (
                    <p className="text-[10px] text-violet-500 italic">Enter value manually</p>
                  )}
                </div>
              );
            })}
          </dl>
            );
          })()}
        </div>
      )}

      {/* ── Table Fields (multiple rows) ─────────────────────────────── */}
      {ocrResult && tableRowCount > 0 && (
        <div className={`border border-border rounded-lg relative overflow-hidden transition-all ${isRunning ? 'shadow-sm bg-muted/10' : ''}`}>
          {isRunning && (
            <div className="absolute inset-x-0 top-0 h-1 bg-primary/20 z-10">
               <div className="h-full bg-primary animate-pulse w-full"></div>
            </div>
          )}
          <div className={`p-4 border-b flex items-center justify-between ${isRunning ? 'bg-muted/20' : 'border-border'}`}>
            <h2 className="font-semibold text-foreground flex items-center gap-2">
              Table Data
              {tableRowCount > 0 && <span className="text-sm font-normal text-muted-foreground">({tableRowCount} rows)</span>}
              {isRunning && <span className="text-xs font-medium text-primary bg-primary/10 px-2 py-0.5 rounded-full animate-pulse flex items-center gap-1.5"><Loader2 className="h-3 w-3 animate-spin"/> Validating Row by Row</span>}
            </h2>
            <p className="text-xs text-muted-foreground flex items-center gap-1">
              <Pencil className="h-3 w-3" /> Click to edit
            </p>
          </div>
          <div className="overflow-x-auto">
            <table className="w-full text-sm">
              <thead>
                <tr className="border-b border-border bg-muted/50">
                  <th className="text-center px-3 py-2 font-medium text-muted-foreground">#</th>
                  {tableColumnNames.map(name => {
                    const isCheckboxCol = fieldConfigMap[name.toLowerCase()]?.isCheckbox ?? false;
                    return (
                      <th key={name} className={`${isCheckboxCol ? 'text-center' : 'text-left'} px-3 py-2 font-medium text-muted-foreground whitespace-nowrap`}>
                        {fieldConfigMap[name.toLowerCase()]?.displayLabel ?? name}
                      </th>
                    );
                  })}
                  <th className="text-center px-3 py-2 font-medium text-muted-foreground">Valid.</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-border">
                {Array.from({ length: tableRowCount }, (_, i) => {
                  const rowConfs = tableColumnNames
                    .map(n => tableFieldGroups[n]?.[i]?.confidence)
                    .filter((c): c is number => c !== undefined);
                  const avgConf = rowConfs.length > 0
                    ? rowConfs.reduce((s, c) => s + c, 0) / rowConfs.length
                    : 1;
                  const rowFieldIds = tableColumnNames
                    .map(n => tableFieldGroups[n]?.[i]?.id)
                    .filter((id): id is string => !!id);
                  const rowValidations = allValidations.filter(v =>
                    v.extractedFieldId && rowFieldIds.includes(v.extractedFieldId));
                  const rowIsPending = rowFieldIds.some(fid => pendingIds.has(fid));
                  const rowStatus = rowValidations.some(v => v.status === 'Failed') ? 'Failed'
                    : rowValidations.some(v => v.status === 'Warning') ? 'Warning'
                    : rowValidations.some(v => v.status === 'Passed') ? 'Passed'
                    : null;
                  const passedV = rowValidations.find(v => v.status === 'Passed' && v.erpResponseField);
                  const passedValue = passedV?.erpResponseField
                    ? getErpValue(passedV.erpReference, passedV.erpResponseField)
                    : undefined;
                  const passedSuccessLabel = passedValue ? `✓ ${passedV!.erpResponseField}: ${passedValue}` : '✓';
                  const warningV = rowValidations.find(v => v.status === 'Warning');
                  const isSettled = rowFieldIds.some(id => settledFieldIds.has(id));
                  return (
                    <tr key={i} className={`hover:bg-muted/30 transition-opacity ${isSettled ? 'opacity-50' : ''} ${avgConf < 0.7 && !isSettled ? 'bg-red-50/50' : ''}`}>
                      <td className="px-3 py-2 text-center text-muted-foreground">{i + 1}</td>
                      {tableColumnNames.map(name => {
                        const cellField = tableFieldGroups[name]?.[i];
                        const cellCfg = fieldConfigMap[name.toLowerCase()];
                        const isCheckboxCol = cellCfg?.isCheckbox ?? false;

                        if (isCheckboxCol) {
                          const rawVal = cellField
                            ? (cellField.isManuallyCorreected ? cellField.correctedValue : (cellField.normalizedValue ?? cellField.rawValue))
                            : undefined;
                          const checked = rawVal === 'true';
                          return (
                            <td key={name} className="px-3 py-2 text-center">
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
                        return (
                          <td
                            key={name}
                            className={`px-3 py-2 transition-colors ${
                              cellPending      ? 'bg-muted/30'
                              : cellStatus === 'Failed'  ? 'bg-red-100/80 border-b border-red-300'
                              : cellStatus === 'Warning' ? 'bg-amber-50/80 border-b border-amber-200'
                              : cellStatus === 'Passed'  ? 'bg-green-50/60'
                              : ''
                            }`}
                          >
                            <EditableCell field={cellField} onSave={save} placeholder="—" />
                          </td>
                        );
                      })}
                      {/* Valid. column */}
                      <td className="px-3 py-2 text-center whitespace-nowrap">
                        {isSettled ? (
                          <span className="text-xs font-medium text-violet-600 px-1.5 py-0.5 bg-violet-50 rounded">Settled</span>
                        ) : rowIsPending ? (
                          <span className="inline-flex items-center gap-1 text-xs text-blue-500">
                            <Loader2 className="h-3 w-3 animate-spin" /> Checking
                          </span>
                        ) : rowStatus === 'Failed' ? (
                          <span className="text-xs font-medium text-red-600">✗ Not found</span>
                        ) : rowStatus === 'Warning' ? (
                          <div className="flex flex-col items-center justify-center text-center w-full" title={warningV?.message}>
                            <span className="text-xs font-medium text-amber-600">⚠ Review</span>
                            {warningV?.message && <span className="text-[10px] text-amber-600/80 leading-tight mt-0.5 whitespace-normal line-clamp-2">{warningV.message}</span>}
                          </div>
                        ) : rowStatus === 'Passed' ? (
                          <span className="text-xs font-medium text-green-600 font-mono">{passedSuccessLabel}</span>
                        ) : (
                          <span className="text-xs text-muted-foreground/40">—</span>
                        )}
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
        </div>
      )}

      {/* ── Admin: PaddleOCR raw extracted text ─────────────────────── */}
      {isAdmin && ocrResult?.rawText && ocrResult.fields.length > 0 && (
        <div className="card p-5 space-y-3">
          <div className="flex items-center justify-between">
            <h2 className="font-semibold text-foreground flex items-center gap-2">
              <FileText className="h-4 w-4" /> PaddleOCR Raw Text
              <span className="text-xs px-1.5 py-0.5 rounded bg-amber-100 text-amber-700 font-medium">Admin</span>
            </h2>
            <button
              className="btn-secondary flex items-center gap-1.5 text-xs"
              disabled={paddleRawRunning}
              onClick={async () => {
                setPaddleRawRunning(true);
                setPaddleRawText(null);
                try {
                  const res = await ocrApi.runPaddleRaw(id!);
                  setPaddleRawText((res.data as { rawText: string }).rawText);
                } finally {
                  setPaddleRawRunning(false);
                }
              }}
              title="Re-run only PaddleOCR engine and show the fresh raw text output"
            >
              {paddleRawRunning
                ? <><Loader2 className="h-3.5 w-3.5 animate-spin" /> Running…</>
                : <><RefreshCw className="h-3.5 w-3.5" /> Re-run PaddleOCR</>}
            </button>
          </div>
          <p className="text-xs text-muted-foreground">
            Full text extracted by PaddleOCR before field parsing. Use this to verify OCR accuracy. Click <strong>Re-run PaddleOCR</strong> to refresh only the raw text without re-extracting fields.
          </p>
          <pre className="text-xs text-foreground bg-muted border border-border rounded p-3 whitespace-pre-wrap break-words max-h-80 overflow-auto font-mono leading-relaxed">
            {paddleRawText ?? ocrResult.rawText}
          </pre>
          {paddleRawText && (
            <p className="text-xs text-green-700 font-medium flex items-center gap-1">
              <CheckCircle className="h-3.5 w-3.5" /> Updated — showing latest PaddleOCR output
            </p>
          )}
        </div>
      )}


      {/* ── Raw text fallback — shown when OCR ran but no structured fields extracted ── */}
      {ocrResult && ocrResult.fields.length === 0 && ocrResult.rawText && (
        <div className="card p-5 space-y-3">
          <h2 className="font-semibold text-foreground flex items-center gap-2">
            <FileText className="h-4 w-4" /> Raw OCR Text
          </h2>
          <p className="text-xs text-amber-600">
            No structured fields were extracted.
            {!doc.documentTypeId
              ? ' Assign a document type above and re-run OCR to extract fields.'
              : ' Re-run OCR to retry extraction.'}
          </p>
          <pre className="text-xs text-foreground bg-muted border border-border rounded p-3 whitespace-pre-wrap break-words max-h-96 overflow-auto">
            {ocrResult.rawText}
          </pre>
        </div>
      )}

      {/* ── Version History ──────────────────────────────────────────── */}
      {doc.versions && doc.versions.length > 1 && (
        <div className="card p-5">
          <h2 className="font-semibold text-foreground flex items-center gap-2 mb-3">
            <History className="h-4 w-4" /> Version History
          </h2>
          <div className="space-y-2">
            {doc.versions.map(v => (
              <div key={v.id} className="flex items-center justify-between text-sm py-2 border-b border-border last:border-0">
                <span className="font-medium text-foreground">Version {v.versionNumber}</span>
                <span className="text-muted-foreground">{new Date(v.uploadedAt).toLocaleString()}</span>
                <span className="text-muted-foreground">{v.uploadedByUsername}</span>
              </div>
            ))}
          </div>
        </div>
      )}

      {/* ── Delete modal ─────────────────────────────────────────────── */}
      {showDeleteModal && (
        <div className="fixed inset-0 z-50 flex items-center justify-center p-4 bg-black/50">
          <div className="card p-6 w-full max-w-md space-y-4">
            <h3 className="font-semibold text-foreground">Delete Document</h3>
            <p className="text-sm text-muted-foreground">
              Are you sure you want to delete <span className="font-medium text-foreground">"{doc.originalFilename}"</span>?
              This action cannot be undone.
            </p>
            <div className="flex justify-end gap-3">
              <button className="btn-secondary" onClick={() => setShowDeleteModal(false)}>Cancel</button>
              <button
                className="btn-primary bg-destructive hover:bg-destructive/90 flex items-center gap-2"
                onClick={() => deleteDoc.mutate()}
                disabled={deleteDoc.isPending}
              >
                {deleteDoc.isPending && <Loader2 className="h-4 w-4 animate-spin" />}
                Delete
              </button>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}

function Row({ label, value }: { label: string; value: React.ReactNode }) {
  return (
    <div className="flex justify-between gap-4">
      <dt className="text-muted-foreground flex-shrink-0">{label}</dt>
      <dd className="text-foreground text-right">{value}</dd>
    </div>
  );
}
