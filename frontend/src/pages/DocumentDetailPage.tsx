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
  AlertTriangle, CheckCircle, Loader2, Clock, History, Edit2, Check, X, Pencil, CheckSquare, RefreshCw, StopCircle,
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

// ─── SummaryCard ──────────────────────────────────────────────────────────────
// Matches StatementSync's EditableSummaryCard

function SummaryCard({
  label, field, onSave, highlight,
}: {
  label: string;
  field: ExtractedField | undefined;
  onSave: (fieldId: string, value: string) => void;
  highlight?: boolean;
}) {
  return (
    <div className={`border rounded-lg p-3 ${highlight ? 'border-primary/50 bg-primary/5' : 'border-border'}`}>
      <p className="text-xs text-muted-foreground">{label}</p>
      <div className={`font-mono font-semibold mt-1 ${highlight ? 'text-lg' : 'text-sm'}`}>
        <EditableCell field={field} onSave={onSave} align="right" placeholder="—" />
      </div>
    </div>
  );
}

// ─── AgingBucket ─────────────────────────────────────────────────────────────
// Matches StatementSync's EditableAgingBucket

function AgingBucket({
  label, field, onSave, warn,
}: {
  label: string;
  field: ExtractedField | undefined;
  onSave: (fieldId: string, value: string) => void;
  warn?: boolean;
}) {
  return (
    <div className={`text-center p-2 rounded ${warn ? 'bg-red-50' : 'bg-muted/50'}`}>
      <p className="text-xs text-muted-foreground">{label}</p>
      <div className={`font-mono font-medium text-sm mt-1 ${warn ? 'text-red-700' : ''}`}>
        <EditableCell field={field} onSave={onSave} align="center" placeholder="—" />
      </div>
    </div>
  );
}

// ─── Document Type labels (from StatementSync) ───────────────────────────────

const DOC_TYPE_LABELS: Record<string, string> = {
  INVOICE: 'Invoice',
  CREDIT_NOTE: 'Credit Note',
  DEBIT_NOTE: 'Debit Note',
  PAYMENT: 'Payment',
  JOURNAL: 'Journal',
  OPENING_BALANCE: 'Opening Balance',
  OTHER: 'Other',
};

// ─── Main Page ────────────────────────────────────────────────────────────────

export default function DocumentDetailPage() {
  const { id } = useParams<{ id: string }>();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const { isAdmin, logout } = useAuth();
  const [showDeleteModal, setShowDeleteModal] = useState(false);
  const [editingType, setEditingType] = useState(false);
  const [pendingTypeId, setPendingTypeId] = useState<string>('');
  const [pendingIds, setPendingIds] = useState<Set<string>>(new Set());
  const [sessionError, setSessionError] = useState<string | null>(null);

  // Tracks doc status across renders to detect the Processing → PendingReview transition.
  const prevStatusRef = useRef<string | undefined>(undefined);

  const { data: doc, isLoading } = useQuery<Document>({
    queryKey: ['document', id],
    queryFn: () => documentApi.getById(id!).then(r => r.data),
    enabled: !!id,
    refetchInterval: q => q.state.data?.status === 'Processing' ? 3000 : false,
  });

  const { data: ocrResult } = useQuery<OcrResult>({
    queryKey: ['ocr-result', id],
    queryFn: () => ocrApi.getResult(id!).then(r => r.data),
    enabled: !!id && doc?.status !== 'Uploaded',
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
    onSuccess: () => {
      // Clear stale validation results immediately so old results don't persist after re-OCR.
      queryClient.setQueryData(['validation', id], []);
      invalidate();
      queryClient.invalidateQueries({ queryKey: ['ocr-result', id] });
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
      setSessionError(null);
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
    onError: (error: unknown) => {
      const status = (error as { response?: { status?: number } })?.response?.status;
      if (status === 424) logout('session_expired');
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

  // ── Field lookup helpers ────────────────────────────────────────────────────
  // Returns the first ExtractedField matching the given fieldName (case-insensitive).
  const getField = useCallback((name: string): ExtractedField | undefined =>
    ocrResult?.fields.find(f => f.fieldName.toLowerCase() === name.toLowerCase()),
  [ocrResult]);

  // Returns all ExtractedFields matching the given fieldName (for multi-value / line item columns).
  const getFields = useCallback((name: string): ExtractedField[] =>
    ocrResult?.fields.filter(f => f.fieldName.toLowerCase() === name.toLowerCase()) ?? [],
  [ocrResult]);

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

  // Groups header fields to validate simultaneously, then table rows sequentially row-by-row.
  const runSequential = useCallback(async (fields: ExtractedField[]) => {
    const validatable = fields.filter(f => {
      const cfg = fieldConfigMap[f.fieldName.toLowerCase()];
      return cfg?.erpMappingKey && !cfg.isManualEntry;
    });

    // Provide a helper to check if a fieldName is a table field (re-implemented to avoid hook dependency cycle)
    const checkIsTableField = (name: string) => {
      const cfg = fieldConfigMap[name.toLowerCase()];
      if (cfg) return cfg.allowMultiple;
      return (fields.filter(f => f.fieldName === name).length) > 1;
    };

    const headerFieldsToRun = validatable.filter(f => !checkIsTableField(f.fieldName));
    const tableFields = validatable.filter(f => checkIsTableField(f.fieldName));

    // 1. Run all header fields simultaneously
    if (headerFieldsToRun.length > 0 && !stopRequestedRef.current) {
      await Promise.allSettled(headerFieldsToRun.map(f => validateField.mutateAsync(f.id)));
    }

    // 2. Group table fields by row index and run row-by-row
    // Find unique column names in tableFields
    const colNames = [...new Set(tableFields.map(f => f.fieldName))];
    const groupedCols: Record<string, ExtractedField[]> = {};
    for (const name of colNames) {
      groupedCols[name] = fields.filter(f => f.fieldName === name);
    }
    const maxRows = Math.max(...Object.values(groupedCols).map(g => g.length), 0);

    for (let i = 0; i < maxRows; i++) {
      if (stopRequestedRef.current) break;
      const rowFields = colNames
        .map(name => groupedCols[name]?.[i])
        .filter((f): f is ExtractedField => !!f && fieldConfigMap[f.fieldName.toLowerCase()]?.erpMappingKey != null);
      if (rowFields.length > 0) {
        // Run cells in the same row simultaneously
        await Promise.allSettled(rowFields.map(f => validateField.mutateAsync(f.id)));
      }
    }
  }, [fieldConfigMap, validateField, stopRequestedRef]);

  const runAllValidations = useCallback(async () => {
    if (!sessionStorage.getItem('acumatica_token')) {
      logout('session_expired');
      return;
    }
    setSessionError(null);
    stopRequestedRef.current = false;
    setIsRunning(true);
    await runSequential(ocrResult?.fields ?? []);
    // Only clear isRunning if the user hasn't already stopped it — prevents a
    // re-render flash where the Stop button briefly reappears as Run Validation.
    if (!stopRequestedRef.current) setIsRunning(false);
  }, [runSequential, logout, ocrResult?.fields]);

  const stopValidation = useCallback(() => {
    stopRequestedRef.current = true;
    setIsRunning(false);
    setPendingIds(new Set());
  }, []);

  const isValidating = isRunning || allFieldIds.some(id => pendingIds.has(id));

  // Auto-run validation when OCR completes (Processing → PendingReview).
  useEffect(() => {
    if (prevStatusRef.current === 'Processing' && doc?.status === 'PendingReview') {
      queryClient.setQueryData(['validation', id], []);
      stopRequestedRef.current = false;
      setIsRunning(true);
      runSequential(ocrResult?.fields ?? []).finally(() => { if (!stopRequestedRef.current) setIsRunning(false); });
    }
    prevStatusRef.current = doc?.status;
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [doc?.status]);

  const isTableField = useCallback((fieldName: string) => {
    const cfg = fieldConfigMap[fieldName.toLowerCase()];
    if (cfg) return cfg.allowMultiple;
    // Unconfigured extra: treat as table if it appears more than once
    return (ocrResult?.fields.filter(f => f.fieldName === fieldName).length ?? 0) > 1;
  }, [fieldConfigMap, ocrResult]);

  // Header fields: single-value, first occurrence of each name
  const headerFields = useMemo(() =>
    ocrResult?.fields.filter((f, i, arr) =>
      !isTableField(f.fieldName) &&
      arr.findIndex(x => x.fieldName === f.fieldName) === i
    ) ?? [],
  [ocrResult, isTableField]);

  // Table fields: grouped by fieldName, preserving config display order
  const tableColumnNames = useMemo(() =>
    [...new Set(ocrResult?.fields
      .filter(f => isTableField(f.fieldName))
      .map(f => f.fieldName) ?? []
    )],
  [ocrResult, isTableField]);

  const tableFieldGroups = useMemo(() => {
    const groups: Record<string, ExtractedField[]> = {};
    for (const name of tableColumnNames)
      groups[name] = ocrResult?.fields.filter(f => f.fieldName === name) ?? [];
    return groups;
  }, [tableColumnNames, ocrResult]);

  const tableRowCount = useMemo(() =>
    Math.max(...Object.values(tableFieldGroups).map(g => g.length), 0),
  [tableFieldGroups]);

  if (isLoading) return <div className="text-center py-12 text-muted-foreground">Loading...</div>;
  if (!doc) return <div className="text-center py-12 text-destructive">Document not found.</div>;

  const canOcr  = ['Uploaded', 'PendingReview', 'ReviewInProgress'].includes(doc.status);
  const isRerun = doc.status !== 'Uploaded';

  const allValidations = validations ?? [];
  const validPassed   = allValidations.filter(v => v.status === 'Passed').length;
  const validFailed   = allValidations.filter(v => v.status === 'Failed').length;
  const validWarnings = allValidations.filter(v => v.status === 'Warning').length;

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

  // Build line items by zipping multi-value column arrays — each index is one row.
  const colDate        = getFields('transactionDate');
  const colDocType     = getFields('documentType');
  const colDocNumber   = getFields('documentNumber');
  const colDesc        = getFields('description');
  const colDebit       = getFields('debitAmount');
  const colCredit      = getFields('creditAmount');
  const colBalance     = getFields('runningBalance');
  const colDueDate     = getFields('dueDate');
  const colPoRef       = getFields('poReference');
  const colSst         = getFields('sstAmount');

  const lineCount = Math.max(
    colDate.length, colDocNumber.length, colDocType.length,
    colDesc.length, colDebit.length, colCredit.length,
    colBalance.length, 0,
  );

  const lineItems = Array.from({ length: lineCount }, (_, i) => ({
    transactionDate: colDate[i],
    documentType:    colDocType[i],
    documentNumber:  colDocNumber[i],
    description:     colDesc[i],
    debitAmount:     colDebit[i],
    creditAmount:    colCredit[i],
    runningBalance:  colBalance[i],
    dueDate:         colDueDate[i],
    poReference:     colPoRef[i],
    sstAmount:       colSst[i],
    // row confidence = average of available fields
    confidence: [colDate[i], colDocNumber[i], colDebit[i], colCredit[i]]
      .filter(Boolean)
      .reduce((s, f) => s + (f!.confidence ?? 1), 0) /
      Math.max([colDate[i], colDocNumber[i], colDebit[i], colCredit[i]].filter(Boolean).length, 1),
  }));

  const hasStatementData = ocrResult && (
    getField('openingBalance') || getField('closingBalance') ||
    getField('statementNumber') || lineCount > 0
  );

  return (
    <div className="space-y-5 max-w-5xl mx-auto">

      {/* ── Header ─────────────────────────────────────────────────────── */}
      <div className="flex items-center gap-3">
        <button onClick={() => navigate('/documents')} className="text-muted-foreground hover:text-foreground">
          <ChevronLeft className="h-5 w-5" />
        </button>
        <div className="flex-1 min-w-0">
          <h1 className="text-xl font-bold text-foreground truncate">{doc.originalFilename}</h1>
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

      {/* ── Notifications ──────────────────────────────────────────────── */}
      {sessionError && (
        <div className="flex items-center gap-2 px-4 py-3 bg-amber-50 border border-amber-200 rounded-lg text-sm text-amber-800">
          <AlertTriangle className="h-4 w-4 flex-shrink-0" />
          <span className="flex-1">{sessionError}</span>
          <button onClick={() => setSessionError(null)} className="text-amber-500 hover:text-amber-700 text-xs">✕</button>
        </div>
      )}
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

      {/* ════════════════════════════════════════════════════════════════
          Statement OCR Data — mirrors StatementSync detail page layout
          ════════════════════════════════════════════════════════════════ */}
      {hasStatementData && (
        <>
          {/* ── Statement header fields ──────────────────────────────── */}
          <div className="card p-5">
            <div className="flex items-start justify-between mb-4">
              <div>
                <div className="flex items-center gap-2 text-sm text-muted-foreground mb-1">
                  <span>Statement</span>
                  <span>/</span>
                  <span className="font-medium text-foreground">
                    <EditableCell
                      field={getField('statementNumber')}
                      onSave={save}
                      placeholder="No statement number"
                    />
                  </span>
                </div>
                <div className="text-sm text-muted-foreground space-x-2">
                  <EditableCell field={getField('statementDate')} onSave={save} placeholder="No date" />
                  <span>•</span>
                  <span>Account: </span>
                  <EditableCell field={getField('customerAccountCode')} onSave={save} placeholder="No account code" />
                </div>
              </div>
              <div className="flex gap-2">
                <span className={`text-xs px-2.5 py-1 rounded-full font-medium ${
                  (ocrResult!.overallConfidence ?? 0) >= 0.9 ? 'bg-green-100 text-green-800'
                  : (ocrResult!.overallConfidence ?? 0) >= 0.7 ? 'bg-amber-100 text-amber-800'
                  : 'bg-red-100 text-red-800'
                }`}>
                  {((ocrResult!.overallConfidence ?? 0) * 100).toFixed(0)}% confidence
                </span>
                {ocrResult!.engineVersion && (
                  <span className="text-xs px-2.5 py-1 rounded-full bg-muted text-muted-foreground">
                    {ocrResult!.engineVersion}
                  </span>
                )}
              </div>
            </div>
            <p className="text-xs text-muted-foreground flex items-center gap-1">
              <Pencil className="h-3 w-3" /> Click any value to edit inline
            </p>
          </div>

          {/* ── Summary cards ────────────────────────────────────────── */}
          <div className="grid grid-cols-2 sm:grid-cols-3 lg:grid-cols-5 gap-3">
            <SummaryCard label="Opening Balance"  field={getField('openingBalance')}  onSave={save} />
            <SummaryCard label="Total Debits"     field={getField('totalDebits')}     onSave={save} />
            <SummaryCard label="Total Credits"    field={getField('totalCredits')}    onSave={save} />
            <SummaryCard label="Total SST"        field={getField('totalSst')}        onSave={save} />
            <SummaryCard label="Closing Balance"  field={getField('closingBalance')}  onSave={save} highlight />
          </div>

          {/* ── Aging summary ─────────────────────────────────────────── */}
          <div className="border border-border rounded-lg p-4">
            <h2 className="font-semibold text-foreground mb-3">Aging Summary</h2>
            <div className="grid grid-cols-2 sm:grid-cols-5 gap-3">
              <AgingBucket label="Current"    field={getField('agingCurrent')}  onSave={save} />
              <AgingBucket label="31-60 Days" field={getField('aging31to60')}   onSave={save} />
              <AgingBucket label="61-90 Days" field={getField('aging61to90')}   onSave={save} />
              <AgingBucket label="91-120 Days"field={getField('aging91to120')}  onSave={save} />
              <AgingBucket label="120+ Days"  field={getField('aging120Plus')}  onSave={save} warn />
            </div>
          </div>

          {/* ── Line items table ──────────────────────────────────────── */}
          <div className="border border-border rounded-lg">
            <div className="p-4 border-b border-border flex items-center justify-between">
              <h2 className="font-semibold text-foreground">
                Line Items{lineCount > 0 ? ` (${lineCount})` : ''}
              </h2>
            </div>

            {lineCount === 0 ? (
              <div className="p-8 text-center text-muted-foreground text-sm">
                No line items extracted. Run OCR to populate line items.
              </div>
            ) : (
              <div className="overflow-x-auto">
                <table className="w-full text-sm">
                  <thead>
                    <tr className="border-b border-border bg-muted/50">
                      <th className="text-left px-3 py-2 font-medium text-muted-foreground">#</th>
                      <th className="text-left px-3 py-2 font-medium text-muted-foreground">Date</th>
                      <th className="text-left px-3 py-2 font-medium text-muted-foreground">Type</th>
                      <th className="text-left px-3 py-2 font-medium text-muted-foreground">Doc #</th>
                      <th className="text-left px-3 py-2 font-medium text-muted-foreground">Description</th>
                      <th className="text-right px-3 py-2 font-medium text-muted-foreground">Debit</th>
                      <th className="text-right px-3 py-2 font-medium text-muted-foreground">Credit</th>
                      <th className="text-right px-3 py-2 font-medium text-muted-foreground">Balance</th>
                      <th className="text-left px-3 py-2 font-medium text-muted-foreground">Due Date</th>
                      <th className="text-left px-3 py-2 font-medium text-muted-foreground">PO Ref</th>
                      <th className="text-right px-3 py-2 font-medium text-muted-foreground">SST</th>
                      <th className="text-center px-3 py-2 font-medium text-muted-foreground">Conf.</th>
                      <th className="text-center px-3 py-2 font-medium text-muted-foreground">Valid.</th>
                    </tr>
                  </thead>
                  <tbody className="divide-y divide-border">
                    {lineItems.map((row, i) => {
                      const isLow = row.confidence < 0.7;
                      const stmtRowFieldIds = [
                        row.transactionDate, row.documentType, row.documentNumber,
                        row.description, row.debitAmount, row.creditAmount,
                        row.runningBalance, row.dueDate, row.poReference, row.sstAmount,
                      ].filter((f): f is ExtractedField => !!f).map(f => f.id);
                      const stmtRowValidations = allValidations.filter(v =>
                        v.extractedFieldId && stmtRowFieldIds.includes(v.extractedFieldId));
                      const stmtRowPending = stmtRowFieldIds.some(fid => pendingIds.has(fid));
                      // Helper: returns Tailwind classes for a single statement cell
                        const stmtCellClass = (f: ExtractedField | undefined) => {
                        if (!f) return '';
                        const s = getValidationStatus(f.id);
                        const p = pendingIds.has(f.id);
                        return p ? 'bg-muted/30' : s === 'Failed' ? 'bg-red-100/80 border-b border-red-300' : s === 'Warning' ? 'bg-amber-50/80 border-b border-amber-200' : s === 'Passed' ? 'bg-green-50/60' : '';
                      };
                      const stmtCellTitle = (f: ExtractedField | undefined) => f ? (allValidations.filter(v => v.extractedFieldId === f.id && v.message).map(v => v.message).join('\n') || undefined) : undefined;
                      const docTypeRaw = row.documentType
                        ? (row.documentType.isManuallyCorreected && row.documentType.correctedValue
                            ? row.documentType.correctedValue
                            : (row.documentType.normalizedValue ?? row.documentType.rawValue ?? ''))
                        : '';
                      return (
                        <tr key={i} className={`hover:bg-muted/30 ${isLow ? 'bg-red-50/50' : ''}`}>
                          <td className="px-3 py-2 text-muted-foreground">{i + 1}</td>
                          <td className={`px-3 py-2 whitespace-nowrap transition-colors ${stmtCellClass(row.transactionDate)}`} title={stmtCellTitle(row.transactionDate)}>
                            <EditableCell field={row.transactionDate} onSave={save} />
                          </td>
                          <td className={`px-3 py-2 transition-colors ${stmtCellClass(row.documentType)}`} title={stmtCellTitle(row.documentType)}>
                            <EditableCell
                              field={row.documentType}
                              onSave={save}
                              placeholder="—"
                            />
                            {docTypeRaw && DOC_TYPE_LABELS[docTypeRaw.toUpperCase()] && (
                              <span className="block text-xs text-muted-foreground">
                                {DOC_TYPE_LABELS[docTypeRaw.toUpperCase()]}
                              </span>
                            )}
                          </td>
                          <td className={`px-3 py-2 font-mono text-xs transition-colors ${stmtCellClass(row.documentNumber)}`} title={stmtCellTitle(row.documentNumber)}>
                            <EditableCell field={row.documentNumber} onSave={save} />
                          </td>
                          <td className={`px-3 py-2 max-w-[180px] transition-colors ${stmtCellClass(row.description)}`} title={stmtCellTitle(row.description)}>
                            <EditableCell field={row.description} onSave={save} />
                          </td>
                          <td className={`px-3 py-2 text-right font-mono transition-colors ${stmtCellClass(row.debitAmount)}`} title={stmtCellTitle(row.debitAmount)}>
                            <EditableCell field={row.debitAmount} onSave={save} align="right" />
                          </td>
                          <td className={`px-3 py-2 text-right font-mono transition-colors ${stmtCellClass(row.creditAmount)}`} title={stmtCellTitle(row.creditAmount)}>
                            <EditableCell field={row.creditAmount} onSave={save} align="right" />
                          </td>
                          <td className={`px-3 py-2 text-right font-mono transition-colors ${stmtCellClass(row.runningBalance)}`} title={stmtCellTitle(row.runningBalance)}>
                            <EditableCell field={row.runningBalance} onSave={save} align="right" />
                          </td>
                          <td className={`px-3 py-2 whitespace-nowrap transition-colors ${stmtCellClass(row.dueDate)}`} title={stmtCellTitle(row.dueDate)}>
                            <EditableCell field={row.dueDate} onSave={save} />
                          </td>
                          <td className={`px-3 py-2 font-mono text-xs transition-colors ${stmtCellClass(row.poReference)}`} title={stmtCellTitle(row.poReference)}>
                            <EditableCell field={row.poReference} onSave={save} />
                          </td>
                          <td className={`px-3 py-2 text-right font-mono transition-colors ${stmtCellClass(row.sstAmount)}`} title={stmtCellTitle(row.sstAmount)}>
                            <EditableCell field={row.sstAmount} onSave={save} align="right" />
                          </td>
                          <td className="px-3 py-2 text-center">
                            <span className={`text-xs px-1.5 py-0.5 rounded-full font-medium ${
                              row.confidence >= 0.9 ? 'bg-green-100 text-green-800'
                              : row.confidence >= 0.7 ? 'bg-amber-100 text-amber-800'
                              : 'bg-red-100 text-red-800'
                            }`}>
                              {Math.round(row.confidence * 100)}%
                            </span>
                          </td>
                          {/* Valid. column — process state only */}
                          <td className="px-3 py-2 text-center whitespace-nowrap">
                            {stmtRowPending ? (
                              <span className="inline-flex items-center gap-1 text-xs text-blue-500">
                                <Loader2 className="h-3 w-3 animate-spin" /> Checking
                              </span>
                            ) : stmtRowValidations.length > 0 ? (
                              <span className="text-xs text-muted-foreground">Done</span>
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
            )}
          </div>

          {/* ── Extraction notes ─────────────────────────────────────── */}
          <div className="border border-border rounded-lg p-4">
            <h2 className="font-semibold text-foreground mb-2">Extraction Notes</h2>
            <div className="text-sm text-muted-foreground">
              <EditableCell
                field={getField('notes') ?? getField('extractionNotes')}
                onSave={save}
                placeholder="No notes — click to add"
                className="whitespace-pre-wrap block"
              />
            </div>
          </div>
        </>
      )}

      {/* ── Header Fields (single-value) ────────────────────────────── */}
      {ocrResult && !hasStatementData && headerFields.length > 0 && (
        <div className={`card p-5 space-y-3 relative overflow-hidden transition-all ${isRunning ? 'bg-muted/30' : ''}`}>
          {isRunning && (
            <div className="absolute inset-x-0 top-0 h-1 bg-primary/20">
              <div className="h-full bg-primary animate-pulse w-full"></div>
            </div>
          )}
          <div className="flex items-center justify-between">
            <h2 className="font-semibold text-foreground flex items-center gap-2">
              Document Fields
              {isRunning && headerFields.some(f => pendingIds.has(f.id)) && <span className="text-xs font-medium text-primary bg-primary/10 px-2 py-0.5 rounded-full animate-pulse flex items-center gap-1.5"><Loader2 className="h-3 w-3 animate-spin"/> Validating...</span>}
            </h2>
            <p className="text-xs text-muted-foreground flex items-center gap-1">
              <Pencil className="h-3 w-3" /> Click any value to edit
            </p>
          </div>
          <div className="divide-y divide-border/50 text-sm">
            {headerFields.map(field => {
              const cfg = fieldConfigMap[field.fieldName.toLowerCase()];
              const label = cfg?.displayLabel ?? field.fieldName;
              const isManual = cfg?.isManualEntry ?? false;
              const isLow = !isManual && (field.confidence ?? 1) < 0.7;
              const status = getValidationStatus(field.id);
              const isPending = pendingIds.has(field.id);
              const msgs = getValidationMsgs(field.id);
              return (
                <div key={field.id} className={`py-2 border-l-2 pl-2 -ml-2 transition-colors ${
                  isManual       ? 'border-l-violet-400 bg-violet-50/40'
                  : isPending    ? 'border-l-muted bg-muted/20'
                  : status === 'Failed'  ? 'border-l-red-400 bg-red-50/60'
                  : status === 'Warning' ? 'border-l-amber-400 bg-amber-50/50'
                  : status === 'Passed'  ? 'border-l-green-400 bg-green-50/30'
                  : isLow        ? 'border-l-red-200 bg-red-50/20'
                  : 'border-l-transparent'
                }`}>
                  <div className="flex justify-between gap-4 items-center">
                    <dt className="text-muted-foreground text-xs font-medium uppercase tracking-wide flex-shrink-0 flex items-center gap-1">
                      {!isManual && isPending        && <Loader2       className="h-3 w-3 text-blue-500 animate-spin" />}
                      {!isManual && !isPending && status === 'Failed'  && <XCircle       className="h-3 w-3 text-red-500" />}
                      {!isManual && !isPending && status === 'Warning' && <AlertTriangle  className="h-3 w-3 text-amber-500" />}
                      {!isManual && !isPending && status === 'Passed'  && <CheckCircle    className="h-3 w-3 text-green-500" />}
                      {label}
                    </dt>
                    <dd className="flex items-center gap-1.5">
                      <EditableCell field={field} onSave={save} align="right" />
                      {isManual
                        ? <span className="text-[10px] px-1.5 py-0.5 rounded-full bg-violet-100 text-violet-700 font-medium flex-shrink-0">Manual</span>
                        : <span className={`text-xs px-1.5 py-0.5 rounded-full font-medium flex-shrink-0 ${
                            isLow ? 'bg-red-100 text-red-800'
                            : (field.confidence ?? 0) >= 0.9 ? 'bg-green-100 text-green-800'
                            : 'bg-amber-100 text-amber-800'
                          }`}>
                            {Math.round((field.confidence ?? 0) * 100)}%
                          </span>
                      }
                    </dd>
                  </div>
                  {isManual && !field.correctedValue && !field.normalizedValue && !field.rawValue && (
                    <p className="text-[10px] text-violet-500 italic mt-0.5">Enter this value manually</p>
                  )}
                  {msgs.map(v => {
                    const erpPassValue = v.status === 'Passed' && v.erpResponseField
                      ? getErpValue(v.erpReference, v.erpResponseField)
                      : undefined;
                    const label = v.status === 'Passed'
                      ? (erpPassValue ? `✓ ${v.erpResponseField}: ${erpPassValue}` : '✓ In ERP')
                      : v.status === 'Warning' ? '⚠ Review'
                      : '✗ Not found';
                    return (
                      <div key={v.id} className="mt-0.5 space-y-0.5">
                        <span className={`text-xs px-1.5 py-0.5 rounded font-medium font-mono ${
                          v.status === 'Passed' ? 'bg-green-100 text-green-700'
                          : v.status === 'Warning' ? 'bg-amber-100 text-amber-700'
                          : 'bg-red-100 text-red-700'
                        }`}>
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

      {/* ── Table Fields (multiple rows) ─────────────────────────────── */}
      {ocrResult && !hasStatementData && tableRowCount > 0 && (
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
                  {tableColumnNames.map(name => (
                    <th key={name} className="text-left px-3 py-2 font-medium text-muted-foreground whitespace-nowrap">
                      {fieldConfigMap[name.toLowerCase()]?.displayLabel ?? name}
                    </th>
                  ))}
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
                  return (
                    <tr key={i} className={`hover:bg-muted/30 ${avgConf < 0.7 ? 'bg-red-50/50' : ''}`}>
                      <td className="px-3 py-2 text-center text-muted-foreground">{i + 1}</td>
                      {tableColumnNames.map(name => {
                        const cellField = tableFieldGroups[name]?.[i];
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
                        {rowIsPending ? (
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
