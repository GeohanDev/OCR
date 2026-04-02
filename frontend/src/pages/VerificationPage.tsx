import { useState, useRef, useEffect, useCallback } from 'react';
import { useParams, useNavigate } from 'react-router-dom';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { documentApi, ocrApi, configApi } from '../api/client';
import FieldReviewPanel from '../components/FieldReviewPanel';
import StatusBadge from '../components/ui/StatusBadge';
import type { Document, OcrResult, ExtractedField, FieldMappingConfig } from '../types';
import { ChevronLeft, ZoomIn, ZoomOut, Loader2, Eye, CheckSquare } from 'lucide-react';
import { Document as PdfDocument, Page as PdfPage, pdfjs } from 'react-pdf';
import 'react-pdf/dist/Page/AnnotationLayer.css';
import 'react-pdf/dist/Page/TextLayer.css';

// Use a static path (copied to public/ in Docker build) to avoid .mjs MIME-type issues with nginx.
pdfjs.GlobalWorkerOptions.workerSrc = '/pdf.worker.min.js';

export default function VerificationPage() {
  const { id } = useParams<{ id: string }>();
  const navigate = useNavigate();
  const queryClient = useQueryClient();

  const [selectedField, setSelectedField] = useState<ExtractedField | null>(null);
  const [currentPage, setCurrentPage] = useState(1);
  const [numPages, setNumPages] = useState(0);
  const [scale, setScale] = useState(1.0);
  const [pdfError, setPdfError] = useState<string | null>(null);
  const [pdfViewerWidth, setPdfViewerWidth] = useState(0);
  const [leftPct, setLeftPct] = useState(58);
  const canvasRef = useRef<HTMLDivElement>(null);
  const isDraggingRef = useRef(false);
  const containerRef = useRef<HTMLDivElement>(null);

  // Force the AppShell <main> to be a bounded, non-scrolling container so the
  // VerificationPage top bar stays pinned and only the inner panels scroll.
  useEffect(() => {
    const main = document.querySelector<HTMLElement>('main');
    const wrapper = main?.parentElement;
    if (!main || !wrapper) return;
    const prevMainOverflow = main.style.overflow;
    const prevWrapperMinH  = wrapper.style.minHeight;
    const prevWrapperH     = wrapper.style.height;
    main.style.overflow    = 'hidden';
    wrapper.style.minHeight = '';
    wrapper.style.height   = '100vh';
    return () => {
      main.style.overflow    = prevMainOverflow;
      wrapper.style.minHeight = prevWrapperMinH;
      wrapper.style.height   = prevWrapperH;
    };
  }, []);

  // Track viewer container width so the PDF page auto-fits without overflow
  useEffect(() => {
    const el = canvasRef.current;
    if (!el) return;
    const ro = new ResizeObserver(() => setPdfViewerWidth(el.clientWidth));
    ro.observe(el);
    return () => ro.disconnect();
  }, []);

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

  // Load signed URL for PDF — use query data directly so cached value works on remount
  const { data: pdfUrl } = useQuery<string>({
    queryKey: ['document-url', id],
    queryFn: () => documentApi.getSignedUrl(id!).then(r => r.data.url),
    enabled: !!id,
    staleTime: 5 * 60 * 1000,
  });

  const { data: fieldConfigs } = useQuery<FieldMappingConfig[]>({
    queryKey: ['field-mappings', doc?.documentTypeId],
    queryFn: () => configApi.getFieldMappings(doc!.documentTypeId!).then(r => r.data),
    enabled: !!doc?.documentTypeId,
  });

  const markChecked = useMutation({
    mutationFn: () => documentApi.updateStatus(id!, 'Checked'),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['document', id] });
      queryClient.invalidateQueries({ queryKey: ['documents'] });
    },
  });

  const fields = ocrResult?.fields ?? [];
  const rawText = ocrResult?.rawText;
  const isPdf = doc?.originalFilename.toLowerCase().endsWith('.pdf');
  const canCheck = doc && !['Uploaded', 'PendingProcess', 'Processing', 'Approved', 'Pushed', 'Checked'].includes(doc.status);

  const selectedBBox = selectedField?.boundingBox;

  // Stable callbacks prevent react-pdf from reloading the document on re-renders.
  const handlePdfLoadSuccess = useCallback(({ numPages }: { numPages: number }) => {
    setNumPages(numPages);
    setPdfError(null);
  }, []);
  const handlePdfLoadError = useCallback((err: Error) => {
    setPdfError(err.message ?? 'Unknown error');
  }, []);

  const startDrag = useCallback((e: React.MouseEvent) => {
    e.preventDefault();
    isDraggingRef.current = true;
    const onMove = (ev: MouseEvent) => {
      if (!isDraggingRef.current || !containerRef.current) return;
      const rect = containerRef.current.getBoundingClientRect();
      const pct = Math.min(80, Math.max(20, ((ev.clientX - rect.left) / rect.width) * 100));
      setLeftPct(pct);
    };
    const onUp = () => {
      isDraggingRef.current = false;
      window.removeEventListener('mousemove', onMove);
      window.removeEventListener('mouseup', onUp);
    };
    window.addEventListener('mousemove', onMove);
    window.addEventListener('mouseup', onUp);
  }, []);

  return (
    <div className="flex flex-col h-full overflow-hidden -m-4 md:-m-6">
      {/* Top bar — flex-shrink-0 keeps it pinned while panels scroll independently */}
      <div className="flex items-center justify-between px-4 py-3 bg-white border-b border-gray-200 flex-shrink-0 z-10">
        <div className="flex items-center gap-3">
          <button onClick={() => navigate(-1)} className="text-gray-500 hover:text-gray-700">
            <ChevronLeft className="h-5 w-5" />
          </button>
          <div>
            <h1 className="text-sm font-semibold text-gray-900 truncate max-w-xs">{doc?.originalFilename}</h1>
            {doc && <StatusBadge status={doc.status} />}
          </div>
        </div>
        <div className="flex items-center gap-2">
          {canCheck && (
            <button
              onClick={() => markChecked.mutate()}
              disabled={markChecked.isPending}
              className="flex items-center gap-1.5 text-sm text-white bg-teal-600 hover:bg-teal-700 disabled:opacity-50 border border-teal-600 rounded-md px-3 py-1.5 transition-colors font-medium"
            >
              {markChecked.isPending
                ? <Loader2 className="h-4 w-4 animate-spin" />
                : <CheckSquare className="h-4 w-4" />}
              Mark as Checked
            </button>
          )}
          {doc?.status === 'Checked' && (
            <span className="text-sm text-teal-700 font-medium flex items-center gap-1">
              <CheckSquare className="h-4 w-4" /> Checked
            </span>
          )}
          {pdfUrl && (
            <button
              onClick={() => window.open(pdfUrl, '_blank')}
              className="flex items-center gap-1.5 text-sm text-gray-600 hover:text-gray-900 border border-gray-200 rounded-md px-3 py-1.5 hover:bg-gray-50 transition-colors"
            >
              <Eye className="h-4 w-4" /> Open File
            </button>
          )}
        </div>
      </div>

      {/* Split pane */}
      <div ref={containerRef} className="flex flex-row flex-1 min-h-0 overflow-hidden select-none">
        {/* Left: Document viewer */}
        <div style={{ width: `${leftPct}%` }} className="flex flex-col min-h-0 overflow-hidden border-r border-gray-200 bg-gray-100">
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

          {/* PDF / image viewer — auto-fits to container width, scrolls when zoomed */}
          <div className="flex-1 overflow-auto p-4" ref={canvasRef}>
            {!pdfUrl ? (
              <div className="flex items-center justify-center h-full text-gray-400">
                <Loader2 className="h-6 w-6 animate-spin" />
              </div>
            ) : pdfError ? (
              <div className="flex flex-col items-center justify-center gap-3 text-red-500 p-6 text-center h-full">
                <p className="text-sm font-medium">Failed to load document</p>
                <p className="text-xs text-gray-500">{pdfError}</p>
                <a href={pdfUrl} target="_blank" rel="noopener noreferrer" className="text-xs text-primary underline">
                  Open file directly
                </a>
              </div>
            ) : isPdf ? (
              <div className="relative inline-block">
                <PdfDocument
                  file={pdfUrl}
                  onLoadSuccess={handlePdfLoadSuccess}
                  onLoadError={handlePdfLoadError}
                  loading={<Loader2 className="h-6 w-6 animate-spin text-gray-400" />}
                >
                  <PdfPage
                    pageNumber={currentPage}
                    width={pdfViewerWidth > 0 ? Math.max(100, pdfViewerWidth - 32) * scale : undefined}
                    renderAnnotationLayer={true}
                    renderTextLayer={true}
                  />
                </PdfDocument>

                {/* Bounding box overlay for selected field */}
                {selectedBBox && selectedBBox.page === currentPage && (
                  <div
                    className="absolute border-2 border-primary bg-primary/20 rounded pointer-events-none"
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
                style={{ maxWidth: '100%', transform: `scale(${scale})`, transformOrigin: 'top left' }}
                className="shadow-lg"
              />
            )}
          </div>
        </div>

        {/* Resize divider */}
        <div
          onMouseDown={startDrag}
          className="w-1.5 bg-gray-200 hover:bg-primary/50 active:bg-primary cursor-col-resize flex-shrink-0 transition-colors"
          title="Drag to resize"
        />

        {/* Right: Field review panel */}
        <div className="flex-1 flex flex-col min-h-0 overflow-hidden bg-white">
          <div className="px-4 py-3 border-b border-gray-200 flex-shrink-0">
            <h2 className="text-sm font-semibold text-gray-700">Extracted Fields</h2>
            <p className="text-xs text-gray-500">{fields.length} fields · click to highlight on document</p>
          </div>
          <div className="flex-1 min-h-0 flex flex-col">
            <FieldReviewPanel
              documentId={id!}
              fields={fields}
              rawText={rawText}
              fieldConfigs={fieldConfigs}
              onFieldSelect={f => { setSelectedField(f); if (f?.boundingBox) setCurrentPage(f.boundingBox.page); }}
              selectedFieldId={selectedField?.id}
            />
          </div>
        </div>
      </div>
    </div>
  );
}
