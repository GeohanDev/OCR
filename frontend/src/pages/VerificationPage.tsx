import { useState, useRef } from 'react';
import { useParams, useNavigate } from 'react-router-dom';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { documentApi, ocrApi, validationApi } from '../api/client';
import { useAuth } from '../contexts/AuthContext';
import FieldReviewPanel from '../components/FieldReviewPanel';
import StatusBadge from '../components/ui/StatusBadge';
import type { Document, OcrResult, ExtractedField } from '../types';
import { ChevronLeft, CheckCircle, XCircle, ZoomIn, ZoomOut, Loader2 } from 'lucide-react';
import { Document as PdfDocument, Page as PdfPage, pdfjs } from 'react-pdf';
import 'react-pdf/dist/Page/AnnotationLayer.css';
import 'react-pdf/dist/Page/TextLayer.css';

pdfjs.GlobalWorkerOptions.workerSrc = new URL(
  'pdfjs-dist/build/pdf.worker.min.mjs',
  import.meta.url,
).toString();

export default function VerificationPage() {
  const { id } = useParams<{ id: string }>();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const { isManagerOrAbove } = useAuth();

  const [selectedField, setSelectedField] = useState<ExtractedField | null>(null);
  const [currentPage, setCurrentPage] = useState(1);
  const [numPages, setNumPages] = useState(0);
  const [scale, setScale] = useState(1.0);
  const [pdfUrl, setPdfUrl] = useState<string | null>(null);
  const [showRejectModal, setShowRejectModal] = useState(false);
  const [rejectReason, setRejectReason] = useState('');
  const canvasRef = useRef<HTMLDivElement>(null);

  const { data: doc } = useQuery<Document>({
    queryKey: ['document', id],
    queryFn: () => documentApi.getById(id!).then(r => r.data),
    enabled: !!id,
  });

  const { data: ocrResult } = useQuery<OcrResult>({
    queryKey: ['ocr-result', id],
    queryFn: () => ocrApi.getResult(id!).then(r => r.data),
    enabled: !!id,
  });

  // Load signed URL for PDF
  useQuery({
    queryKey: ['document-url', id],
    queryFn: async () => {
      const res = await documentApi.getSignedUrl(id!);
      setPdfUrl(res.data.url);
      return res.data.url;
    },
    enabled: !!id,
    staleTime: 5 * 60 * 1000,
  });

  const invalidate = () => {
    queryClient.invalidateQueries({ queryKey: ['document', id] });
    queryClient.invalidateQueries({ queryKey: ['documents'] });
  };

  const approve = useMutation({
    mutationFn: () => validationApi.approve(id!),
    onSuccess: () => { invalidate(); navigate(`/documents/${id}`); },
  });

  const reject = useMutation({
    mutationFn: () => validationApi.reject(id!, rejectReason),
    onSuccess: () => { invalidate(); setShowRejectModal(false); navigate(`/documents/${id}`); },
  });

  const fields = ocrResult?.fields ?? [];
  const isPdf = doc?.originalFilename.toLowerCase().endsWith('.pdf');

  const selectedBBox = selectedField?.boundingBox;

  return (
    <div className="flex flex-col h-full -m-4 md:-m-6">
      {/* Top bar */}
      <div className="flex items-center justify-between px-4 py-3 bg-white border-b border-gray-200 flex-shrink-0">
        <div className="flex items-center gap-3">
          <button onClick={() => navigate(`/documents/${id}`)} className="text-gray-500 hover:text-gray-700">
            <ChevronLeft className="h-5 w-5" />
          </button>
          <div>
            <h1 className="text-sm font-semibold text-gray-900 truncate max-w-xs">{doc?.originalFilename}</h1>
            {doc && <StatusBadge status={doc.status} />}
          </div>
        </div>
      </div>

      {/* Split pane */}
      <div className="flex flex-col md:flex-row flex-1 overflow-hidden">
        {/* Left: Document viewer */}
        <div className="flex flex-col flex-1 overflow-hidden border-b md:border-b-0 md:border-r border-gray-200 bg-gray-100 min-h-0 md:min-h-full">
          {/* Viewer toolbar */}
          <div className="flex items-center justify-between px-3 py-2 bg-white border-b border-gray-200 flex-shrink-0">
            <div className="flex items-center gap-2">
              <button
                className="p-1 rounded hover:bg-gray-100 disabled:opacity-40"
                disabled={currentPage <= 1}
                onClick={() => setCurrentPage(p => p - 1)}
              >◀</button>
              <span className="text-sm text-gray-600">{currentPage} / {numPages || '?'}</span>
              <button
                className="p-1 rounded hover:bg-gray-100 disabled:opacity-40"
                disabled={currentPage >= numPages}
                onClick={() => setCurrentPage(p => p + 1)}
              >▶</button>
            </div>
            <div className="flex items-center gap-2">
              <button className="p-1 rounded hover:bg-gray-100" onClick={() => setScale(s => Math.max(0.5, s - 0.25))}>
                <ZoomOut className="h-4 w-4" />
              </button>
              <span className="text-xs text-gray-500 w-12 text-center">{Math.round(scale * 100)}%</span>
              <button className="p-1 rounded hover:bg-gray-100" onClick={() => setScale(s => Math.min(3, s + 0.25))}>
                <ZoomIn className="h-4 w-4" />
              </button>
            </div>
          </div>

          {/* PDF / image viewer */}
          <div className="flex-1 overflow-auto flex justify-center p-4" ref={canvasRef}>
            {!pdfUrl ? (
              <div className="flex items-center justify-center text-gray-400">
                <Loader2 className="h-6 w-6 animate-spin" />
              </div>
            ) : isPdf ? (
              <div className="relative">
                <PdfDocument
                  file={pdfUrl}
                  onLoadSuccess={({ numPages }) => setNumPages(numPages)}
                  loading={<Loader2 className="h-6 w-6 animate-spin text-gray-400" />}
                >
                  <PdfPage
                    pageNumber={currentPage}
                    scale={scale}
                    renderAnnotationLayer={true}
                    renderTextLayer={true}
                  />
                </PdfDocument>

                {/* Bounding box overlay for selected field */}
                {selectedBBox && selectedBBox.page === currentPage && (
                  <div
                    className="absolute border-2 border-blue-500 bg-blue-100/30 rounded pointer-events-none"
                    style={{
                      left: selectedBBox.x * scale,
                      top: selectedBBox.y * scale,
                      width: selectedBBox.w * scale,
                      height: selectedBBox.h * scale,
                    }}
                  />
                )}
              </div>
            ) : (
              <img
                src={pdfUrl}
                alt="Document"
                style={{ transform: `scale(${scale})`, transformOrigin: 'top center' }}
                className="max-w-full shadow-lg"
              />
            )}
          </div>
        </div>

        {/* Right: Field review panel */}
        <div className="w-full md:w-96 flex flex-col overflow-hidden bg-white">
          <div className="px-4 py-3 border-b border-gray-200 flex-shrink-0">
            <h2 className="text-sm font-semibold text-gray-700">Extracted Fields</h2>
            <p className="text-xs text-gray-500">{fields.length} fields · click to highlight on document</p>
          </div>
          <div className="flex-1 overflow-hidden">
            <FieldReviewPanel
              documentId={id!}
              fields={fields}
              onFieldSelect={f => { setSelectedField(f); if (f?.boundingBox) setCurrentPage(f.boundingBox.page); }}
              selectedFieldId={selectedField?.id}
            />
          </div>
        </div>
      </div>

      {/* Sticky footer — approve / reject */}
      {isManagerOrAbove && doc && ['PendingReview', 'ReviewInProgress'].includes(doc.status) && (
        <div className="flex-shrink-0 bg-white border-t border-gray-200 px-4 py-3 flex items-center justify-end gap-3">
          <button
            onClick={() => setShowRejectModal(true)}
            className="btn-secondary flex items-center gap-2 text-red-600 border-red-200 hover:bg-red-50"
          >
            <XCircle className="h-4 w-4" /> Reject
          </button>
          <button
            onClick={() => approve.mutate()}
            disabled={approve.isPending}
            className="btn-primary flex items-center gap-2 bg-green-600 hover:bg-green-700"
          >
            {approve.isPending ? <Loader2 className="h-4 w-4 animate-spin" /> : <CheckCircle className="h-4 w-4" />}
            Approve
          </button>
        </div>
      )}

      {/* Reject modal */}
      {showRejectModal && (
        <div className="fixed inset-0 z-50 flex items-center justify-center p-4 bg-black/50">
          <div className="card p-6 w-full max-w-md space-y-4">
            <h3 className="font-semibold text-gray-900">Reject Document</h3>
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
