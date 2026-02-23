import { useState } from 'react';
import { useParams, useNavigate, Link } from 'react-router-dom';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { documentApi, ocrApi, validationApi, erpApi } from '../api/client';
import { useAuth } from '../contexts/AuthContext';
import StatusBadge from '../components/ui/StatusBadge';
import type { Document, OcrResult } from '../types';
import {
  ChevronLeft, Cpu, CheckCircle, XCircle, Send, Eye, FileText,
  AlertTriangle, Loader2, Clock, History
} from 'lucide-react';

export default function DocumentDetailPage() {
  const { id } = useParams<{ id: string }>();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const { isManagerOrAbove } = useAuth();
  const [rejectReason, setRejectReason] = useState('');
  const [showRejectModal, setShowRejectModal] = useState(false);

  const { data: doc, isLoading } = useQuery<Document>({
    queryKey: ['document', id],
    queryFn: () => documentApi.getById(id!).then(r => r.data),
    enabled: !!id,
  });

  const { data: ocrResult } = useQuery<OcrResult>({
    queryKey: ['ocr-result', id],
    queryFn: () => ocrApi.getResult(id!).then(r => r.data),
    enabled: !!id && doc?.status !== 'Uploaded',
    retry: false,
  });

  const invalidate = () => {
    queryClient.invalidateQueries({ queryKey: ['document', id] });
    queryClient.invalidateQueries({ queryKey: ['documents'] });
  };

  const triggerOcr = useMutation({
    mutationFn: () => ocrApi.process(id!),
    onSuccess: () => invalidate(),
  });

  const runValidation = useMutation({
    mutationFn: () => validationApi.run(id!),
    onSuccess: () => {
      invalidate();
      queryClient.invalidateQueries({ queryKey: ['validation', id] });
    },
  });

  const approve = useMutation({
    mutationFn: () => validationApi.approve(id!),
    onSuccess: () => invalidate(),
  });

  const reject = useMutation({
    mutationFn: () => validationApi.reject(id!, rejectReason),
    onSuccess: () => { invalidate(); setShowRejectModal(false); },
  });

  const push = useMutation({
    mutationFn: () => erpApi.push(id!),
    onSuccess: () => invalidate(),
  });

  const getSignedUrl = async () => {
    const res = await documentApi.getSignedUrl(id!);
    window.open(res.data.url, '_blank');
  };

  if (isLoading) return <div className="text-center py-12 text-gray-500">Loading...</div>;
  if (!doc) return <div className="text-center py-12 text-red-500">Document not found.</div>;

  const canOcr = doc.status === 'Uploaded';
  const canValidate = ['PendingReview', 'ReviewInProgress'].includes(doc.status);
  const canApprove = isManagerOrAbove && ['PendingReview', 'ReviewInProgress'].includes(doc.status);
  const canPush = isManagerOrAbove && doc.status === 'Approved';

  return (
    <div className="space-y-5 max-w-4xl mx-auto">
      {/* Header */}
      <div className="flex items-center gap-3">
        <button onClick={() => navigate('/documents')} className="text-gray-500 hover:text-gray-700">
          <ChevronLeft className="h-5 w-5" />
        </button>
        <div className="flex-1 min-w-0">
          <h1 className="text-xl font-bold text-gray-900 truncate">{doc.originalFilename}</h1>
          <p className="text-sm text-gray-500">{doc.documentTypeName ?? 'Unknown type'}</p>
        </div>
        <StatusBadge status={doc.status} />
      </div>

      {/* Action Bar */}
      <div className="card p-4 flex flex-wrap gap-2">
        <button onClick={getSignedUrl} className="btn-secondary flex items-center gap-2 text-sm">
          <Eye className="h-4 w-4" /> View File
        </button>

        {canOcr && (
          <button
            onClick={() => triggerOcr.mutate()}
            disabled={triggerOcr.isPending}
            className="btn-primary flex items-center gap-2 text-sm"
          >
            {triggerOcr.isPending ? <Loader2 className="h-4 w-4 animate-spin" /> : <Cpu className="h-4 w-4" />}
            Run OCR
          </button>
        )}

        {doc.status !== 'Uploaded' && doc.status !== 'Processing' && (
          <Link to={`/documents/${id}/verify`} className="btn-secondary flex items-center gap-2 text-sm">
            <FileText className="h-4 w-4" /> Review Fields
          </Link>
        )}

        {canValidate && (
          <button
            onClick={() => runValidation.mutate()}
            disabled={runValidation.isPending}
            className="btn-secondary flex items-center gap-2 text-sm"
          >
            {runValidation.isPending ? <Loader2 className="h-4 w-4 animate-spin" /> : <AlertTriangle className="h-4 w-4" />}
            Run Validation
          </button>
        )}

        {canApprove && (
          <>
            <button
              onClick={() => approve.mutate()}
              disabled={approve.isPending}
              className="btn-primary flex items-center gap-2 text-sm bg-green-600 hover:bg-green-700"
            >
              {approve.isPending ? <Loader2 className="h-4 w-4 animate-spin" /> : <CheckCircle className="h-4 w-4" />}
              Approve
            </button>
            <button
              onClick={() => setShowRejectModal(true)}
              className="btn-secondary flex items-center gap-2 text-sm text-red-600 border-red-200 hover:bg-red-50"
            >
              <XCircle className="h-4 w-4" /> Reject
            </button>
          </>
        )}

        {canPush && (
          <button
            onClick={() => push.mutate()}
            disabled={push.isPending}
            className="btn-primary flex items-center gap-2 text-sm bg-purple-600 hover:bg-purple-700"
          >
            {push.isPending ? <Loader2 className="h-4 w-4 animate-spin" /> : <Send className="h-4 w-4" />}
            Push to ERP
          </button>
        )}
      </div>

      {/* Error notifications */}
      {approve.error && (
        <div className="bg-red-50 border border-red-200 rounded-lg p-4 text-sm text-red-700">
          {(approve.error as { response?: { data?: { detail?: string } } })?.response?.data?.detail ?? 'Approval failed'}
        </div>
      )}

      <div className="grid md:grid-cols-2 gap-5">
        {/* Metadata */}
        <div className="card p-5 space-y-3">
          <h2 className="font-semibold text-gray-900 flex items-center gap-2">
            <FileText className="h-4 w-4" /> Document Info
          </h2>
          <dl className="space-y-2 text-sm">
            <Row label="Filename" value={doc.originalFilename} />
            <Row label="Type" value={doc.documentTypeName ?? '—'} />
            <Row label="Status" value={<StatusBadge status={doc.status} />} />
            <Row label="Uploaded by" value={doc.uploadedByUsername} />
            <Row label="Uploaded at" value={new Date(doc.uploadedAt).toLocaleString()} />
            {doc.processedAt && <Row label="Processed at" value={new Date(doc.processedAt).toLocaleString()} />}
            {doc.reviewedByUsername && <Row label="Reviewed by" value={doc.reviewedByUsername} />}
            {doc.approvedByUsername && <Row label="Approved by" value={doc.approvedByUsername} />}
            {doc.approvedAt && <Row label="Approved at" value={new Date(doc.approvedAt).toLocaleString()} />}
            {doc.pushedAt && <Row label="Pushed at" value={new Date(doc.pushedAt).toLocaleString()} />}
            {doc.notes && <Row label="Notes" value={doc.notes} />}
          </dl>
        </div>

        {/* OCR Summary */}
        <div className="card p-5 space-y-3">
          <h2 className="font-semibold text-gray-900 flex items-center gap-2">
            <Cpu className="h-4 w-4" /> OCR Summary
          </h2>
          {!ocrResult ? (
            <p className="text-sm text-gray-500">
              {doc.status === 'Uploaded'
                ? 'OCR has not been run yet.'
                : doc.status === 'Processing'
                  ? <span className="flex items-center gap-2"><Clock className="h-4 w-4 animate-spin" /> Processing...</span>
                  : 'No OCR result available.'}
            </p>
          ) : (
            <dl className="space-y-2 text-sm">
              <Row label="Pages" value={String(ocrResult.pageCount)} />
              <Row label="Overall confidence" value={`${(ocrResult.overallConfidence * 100).toFixed(1)}%`} />
              <Row label="Fields extracted" value={String(ocrResult.fields.length)} />
              <Row
                label="Low confidence"
                value={String(ocrResult.fields.filter(f => f.confidence < 0.75).length)}
              />
              <Row label="Engine" value={ocrResult.engineVersion} />
              <Row label="Processing time" value={`${ocrResult.processingMs} ms`} />
            </dl>
          )}
        </div>
      </div>

      {/* Version History */}
      {doc.versions && doc.versions.length > 1 && (
        <div className="card p-5">
          <h2 className="font-semibold text-gray-900 flex items-center gap-2 mb-3">
            <History className="h-4 w-4" /> Version History
          </h2>
          <div className="space-y-2">
            {doc.versions.map(v => (
              <div key={v.id} className="flex items-center justify-between text-sm py-2 border-b border-gray-100 last:border-0">
                <span className="font-medium text-gray-700">Version {v.versionNumber}</span>
                <span className="text-gray-500">{new Date(v.uploadedAt).toLocaleString()}</span>
                <span className="text-gray-500">{v.uploadedByUsername}</span>
              </div>
            ))}
          </div>
        </div>
      )}

      {/* Reject modal */}
      {showRejectModal && (
        <div className="fixed inset-0 z-50 flex items-center justify-center p-4 bg-black/50">
          <div className="card p-6 w-full max-w-md space-y-4">
            <h3 className="font-semibold text-gray-900">Reject Document</h3>
            <p className="text-sm text-gray-600">Please provide a reason for rejection.</p>
            <textarea
              className="input w-full h-24 resize-none"
              placeholder="Rejection reason..."
              value={rejectReason}
              onChange={e => setRejectReason(e.target.value)}
            />
            <div className="flex justify-end gap-3">
              <button className="btn-secondary" onClick={() => setShowRejectModal(false)}>Cancel</button>
              <button
                className="btn-primary bg-red-600 hover:bg-red-700"
                onClick={() => reject.mutate()}
                disabled={reject.isPending || !rejectReason.trim()}
              >
                {reject.isPending ? <Loader2 className="h-4 w-4 animate-spin" /> : 'Confirm Reject'}
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
      <dt className="text-gray-500 flex-shrink-0">{label}</dt>
      <dd className="text-gray-900 text-right">{value}</dd>
    </div>
  );
}
