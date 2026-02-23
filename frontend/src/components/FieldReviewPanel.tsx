import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { ocrApi, validationApi } from '../api/client';
import type { ExtractedField, ValidationResult } from '../types';
import { CheckCircle, XCircle, AlertTriangle, Edit2, Check, X, ChevronDown, ChevronUp } from 'lucide-react';

interface FieldReviewPanelProps {
  documentId: string;
  fields: ExtractedField[];
  onFieldSelect?: (field: ExtractedField | null) => void;
  selectedFieldId?: string;
}

function ConfidenceBadge({ value }: { value: number }) {
  const pct = Math.round(value * 100);
  const color = pct >= 80 ? 'text-green-700 bg-green-100' : pct >= 60 ? 'text-orange-700 bg-orange-100' : 'text-red-700 bg-red-100';
  return <span className={`badge text-xs font-mono ${color}`}>{pct}%</span>;
}

function ValidationIcon({ status }: { status: string }) {
  if (status === 'Passed') return <CheckCircle className="h-4 w-4 text-green-500 flex-shrink-0" />;
  if (status === 'Failed') return <XCircle className="h-4 w-4 text-red-500 flex-shrink-0" />;
  if (status === 'Warning') return <AlertTriangle className="h-4 w-4 text-orange-500 flex-shrink-0" />;
  return null;
}

interface FieldRowProps {
  field: ExtractedField;
  validations: ValidationResult[];
  isSelected: boolean;
  onSelect: () => void;
  onCorrect: (fieldId: string, value: string) => void;
  isSaving: boolean;
}

function FieldRow({ field, validations, isSelected, onSelect, onCorrect, isSaving }: FieldRowProps) {
  const [editing, setEditing] = useState(false);
  const [draftValue, setDraftValue] = useState('');

  const displayValue = field.isManuallyCorreected ? field.correctedValue : field.normalizedValue ?? field.rawValue ?? '—';
  const fieldValidations = validations.filter(v => v.fieldName === field.fieldName);
  const worstStatus = fieldValidations.some(v => v.status === 'Failed') ? 'Failed'
    : fieldValidations.some(v => v.status === 'Warning') ? 'Warning'
    : fieldValidations.some(v => v.status === 'Passed') ? 'Passed'
    : null;

  const startEdit = () => {
    setDraftValue(displayValue ?? '');
    setEditing(true);
  };

  const save = () => {
    onCorrect(field.id, draftValue);
    setEditing(false);
  };

  const cancel = () => setEditing(false);

  return (
    <div
      className={`px-4 py-3 cursor-pointer transition-colors ${isSelected ? 'bg-blue-50 border-l-2 border-blue-500' : 'hover:bg-gray-50 border-l-2 border-transparent'}`}
      onClick={onSelect}
    >
      <div className="flex items-start justify-between gap-2 mb-1">
        <span className="text-xs font-medium text-gray-500 uppercase tracking-wide">{field.fieldName}</span>
        <div className="flex items-center gap-1.5">
          <ConfidenceBadge value={field.confidence} />
          {worstStatus && <ValidationIcon status={worstStatus} />}
          {field.isManuallyCorreected && (
            <span className="badge text-xs bg-blue-100 text-blue-700">Corrected</span>
          )}
        </div>
      </div>

      {editing ? (
        <div className="flex items-center gap-2 mt-1" onClick={e => e.stopPropagation()}>
          <input
            className="input text-sm flex-1 py-1"
            value={draftValue}
            onChange={e => setDraftValue(e.target.value)}
            onKeyDown={e => { if (e.key === 'Enter') save(); if (e.key === 'Escape') cancel(); }}
            autoFocus
          />
          <button onClick={save} disabled={isSaving} className="text-green-600 hover:text-green-700">
            <Check className="h-4 w-4" />
          </button>
          <button onClick={cancel} className="text-gray-400 hover:text-gray-600">
            <X className="h-4 w-4" />
          </button>
        </div>
      ) : (
        <div className="flex items-center gap-2">
          <span className="text-sm text-gray-900 flex-1 break-all">{displayValue}</span>
          <button
            onClick={e => { e.stopPropagation(); startEdit(); }}
            className="text-gray-400 hover:text-gray-600 flex-shrink-0"
          >
            <Edit2 className="h-3.5 w-3.5" />
          </button>
        </div>
      )}

      {fieldValidations.length > 0 && (
        <div className="mt-1.5 space-y-0.5">
          {fieldValidations.map(v => (
            <p key={v.id} className={`text-xs ${v.status === 'Failed' ? 'text-red-600' : v.status === 'Warning' ? 'text-orange-600' : 'text-green-600'}`}>
              {v.message}
            </p>
          ))}
        </div>
      )}
    </div>
  );
}

export default function FieldReviewPanel({ documentId, fields, onFieldSelect, selectedFieldId }: FieldReviewPanelProps) {
  const queryClient = useQueryClient();
  const [showSummary, setShowSummary] = useState(true);

  const { data: validations } = useQuery<ValidationResult[]>({
    queryKey: ['validation', documentId],
    queryFn: () => validationApi.getResults(documentId).then(r => r.data),
    retry: false,
  });

  const correctField = useMutation({
    mutationFn: ({ fieldId, value }: { fieldId: string; value: string }) =>
      ocrApi.correctField(documentId, fieldId, value),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['ocr-result', documentId] });
    },
  });

  const allValidations = validations ?? [];
  const passed = allValidations.filter(v => v.status === 'Passed').length;
  const failed = allValidations.filter(v => v.status === 'Failed').length;
  const warnings = allValidations.filter(v => v.status === 'Warning').length;
  const canApprove = failed === 0 && allValidations.length > 0;

  return (
    <div className="flex flex-col h-full">
      {/* Validation summary bar */}
      {allValidations.length > 0 && (
        <div className="border-b border-gray-200 bg-white">
          <button
            className="w-full flex items-center justify-between px-4 py-3"
            onClick={() => setShowSummary(s => !s)}
          >
            <div className="flex items-center gap-3">
              {canApprove
                ? <CheckCircle className="h-5 w-5 text-green-500" />
                : <XCircle className="h-5 w-5 text-red-500" />}
              <span className="text-sm font-medium text-gray-700">
                {canApprove ? 'Ready for approval' : `${failed} blocking issue(s)`}
              </span>
            </div>
            <div className="flex items-center gap-3">
              {passed > 0 && <span className="text-xs text-green-600 font-medium">{passed} passed</span>}
              {warnings > 0 && <span className="text-xs text-orange-600 font-medium">{warnings} warnings</span>}
              {failed > 0 && <span className="text-xs text-red-600 font-medium">{failed} failed</span>}
              {showSummary ? <ChevronUp className="h-4 w-4 text-gray-400" /> : <ChevronDown className="h-4 w-4 text-gray-400" />}
            </div>
          </button>
        </div>
      )}

      {/* Field list */}
      <div className="flex-1 overflow-auto">
        {fields.length === 0 ? (
          <p className="p-6 text-sm text-gray-500 text-center">No fields extracted yet.</p>
        ) : (
          <div className="divide-y divide-gray-100">
            {fields.map(field => (
              <FieldRow
                key={field.id}
                field={field}
                validations={allValidations}
                isSelected={selectedFieldId === field.id}
                onSelect={() => onFieldSelect?.(selectedFieldId === field.id ? null : field)}
                onCorrect={(fieldId, value) => correctField.mutate({ fieldId, value })}
                isSaving={correctField.isPending}
              />
            ))}
          </div>
        )}
      </div>
    </div>
  );
}
