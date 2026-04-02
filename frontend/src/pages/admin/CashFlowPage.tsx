import { useState, useEffect, useRef } from 'react';
import { useQuery, useMutation } from '@tanstack/react-query';
import { useNavigate } from 'react-router-dom';
import { cashFlowApi } from '../../api/client';
import { registerBackgroundJob, unregisterBackgroundJob } from '../../hooks/backgroundActivity';
import {
  RefreshCw, TrendingUp, AlertTriangle, Clock, CheckCircle,
  Building2, FileText, Info, Loader2, ChevronDown, ChevronRight, Users, Download,
} from 'lucide-react';

// ── Types ──────────────────────────────────────────────────────────────────

interface StatementAging {
  documentId: string;
  documentStatus: string;
  statementDate: string | null;
  current: number | null;
  aging30: number | null;
  aging60: number | null;
  aging90: number | null;
  aging90Plus: number | null;
  outstandingBalance: number | null;
  totalInvoiceAmount: number | null;
}

interface ErpAging {
  current: number;
  aging30: number;
  aging60: number;
  aging90: number;
  aging90Plus: number;
  totalOutstanding: number;
  billCount: number;
  snapshotDate?: string | null;
}

interface VendorAgingRow {
  vendorLocalId: string;
  acumaticaVendorId: string;
  vendorName: string;
  paymentTerms: string | null;
  managerId: string;
  managerName: string;
  statement: StatementAging;
  erp: ErpAging;
}

interface AgingSummary {
  totalCurrent: number;
  totalAging30: number;
  totalAging60: number;
  totalAging90: number;
  totalAging90Plus: number;
  totalOutstanding: number;
  vendorCount: number;
  snapshotMissing?: boolean;
}

interface AgingReport {
  asOf: string;
  summary: AgingSummary;
  vendors: VendorAgingRow[];
}

interface SnapshotVendor {
  vendorLocalId: string;
  acumaticaVendorId: string | null;
  vendorName: string;
  current: number;
  aging30: number;
  aging60: number;
  aging90: number;
  aging90Plus: number;
  totalOutstanding: number;
}

interface SnapshotReport {
  snapshotDate: string | null;
  capturedAt: string | null;
  selectedBranch: string | null;
  branches: { branchId: string }[];
  summary: AgingSummary;
  vendors: SnapshotVendor[];
}

// ── Helpers ────────────────────────────────────────────────────────────────

const fmt = (n: number | null | undefined) =>
  n == null ? '—' : n.toLocaleString('en-MY', { minimumFractionDigits: 2, maximumFractionDigits: 2 });

const fmtShort = (n: number) => {
  if (n >= 1_000_000) return `${(n / 1_000_000).toFixed(1)}M`;
  if (n >= 1_000)     return `${(n / 1_000).toFixed(1)}K`;
  return n.toLocaleString('en-MY', { minimumFractionDigits: 0, maximumFractionDigits: 0 });
};

function statusBadge(status: string) {
  const map: Record<string, string> = {
    Checked:  'bg-green-100 text-green-700',
    Approved: 'bg-blue-100 text-blue-700',
    Pushed:   'bg-purple-100 text-purple-700',
  };
  return (
    <span className={`inline-flex items-center px-1.5 py-0.5 rounded text-[10px] font-medium ${map[status] ?? 'bg-gray-100 text-gray-600'}`}>
      {status}
    </span>
  );
}

/** Color class for an aging amount relative to the total. */
function amtClass(amount: number, bucket: 'current' | '30' | '60' | '90' | '91plus') {
  if (amount === 0) return 'text-muted-foreground';
  if (bucket === 'current') return 'text-green-700 font-medium';
  if (bucket === '30')      return 'text-yellow-700 font-medium';
  if (bucket === '60')      return 'text-orange-600 font-medium';
  if (bucket === '90')      return 'text-orange-700 font-semibold';
  return 'text-red-600 font-semibold';
}

/** Compact stacked bar showing aging distribution for a vendor. */
function AgingBar({ erp, total }: { erp: ErpAging; total: number }) {
  if (total === 0) return <div className="h-1.5 rounded-full bg-muted w-full" />;
  const pct = (n: number) => Math.max((n / total) * 100, 0);
  return (
    <div className="flex h-1.5 w-full rounded-full overflow-hidden gap-px">
      {erp.current     > 0 && <div className="bg-green-500"   style={{ width: `${pct(erp.current)}%` }}     title={`Current: ${fmt(erp.current)}`} />}
      {erp.aging30     > 0 && <div className="bg-yellow-400"  style={{ width: `${pct(erp.aging30)}%` }}     title={`1-30d: ${fmt(erp.aging30)}`} />}
      {erp.aging60     > 0 && <div className="bg-orange-400"  style={{ width: `${pct(erp.aging60)}%` }}     title={`31-60d: ${fmt(erp.aging60)}`} />}
      {erp.aging90     > 0 && <div className="bg-orange-600"  style={{ width: `${pct(erp.aging90)}%` }}     title={`61-90d: ${fmt(erp.aging90)}`} />}
      {erp.aging90Plus > 0 && <div className="bg-red-500"     style={{ width: `${pct(erp.aging90Plus)}%` }} title={`91+d: ${fmt(erp.aging90Plus)}`} />}
    </div>
  );
}

function AgingBarFlat({ current, aging30, aging60, aging90, aging90Plus, total }: { current: number; aging30: number; aging60: number; aging90: number; aging90Plus: number; total: number }) {
  if (total === 0) return <div className="h-1.5 rounded-full bg-muted w-full" />;
  const pct = (n: number) => Math.max((n / total) * 100, 0);
  return (
    <div className="flex h-1.5 w-full rounded-full overflow-hidden gap-px">
      {current     > 0 && <div className="bg-green-500"  style={{ width: `${pct(current)}%` }} />}
      {aging30     > 0 && <div className="bg-yellow-400" style={{ width: `${pct(aging30)}%` }} />}
      {aging60     > 0 && <div className="bg-orange-400" style={{ width: `${pct(aging60)}%` }} />}
      {aging90     > 0 && <div className="bg-orange-600" style={{ width: `${pct(aging90)}%` }} />}
      {aging90Plus > 0 && <div className="bg-red-500"    style={{ width: `${pct(aging90Plus)}%` }} />}
    </div>
  );
}

// ── Main component ─────────────────────────────────────────────────────────

const LAYOUT_KEY = 'cashflow-layout';

export default function CashFlowPage() {
  const navigate = useNavigate();
  const [mainTab, setMainTab] = useState<'live' | 'snapshot'>('live');
  const [expanded, setExpanded] = useState<Set<string>>(new Set());
  const [sortCol, setSortCol] = useState<'vendor' | 'current' | '30' | '60' | '90' | '91plus' | 'total'>('vendor');
  const [sortDir, setSortDir] = useState<'asc' | 'desc'>('asc');
  const scrollRestored = useRef(false);

  // Report date for Excel export (defaults to today)
  const todayStr = new Date().toISOString().slice(0, 10);
  const [reportDate, setReportDate] = useState<string>(todayStr);
  const [exporting, setExporting] = useState(false);

  // Restore layout state saved before navigating to a document
  useEffect(() => {
    const raw = sessionStorage.getItem(LAYOUT_KEY);
    if (!raw) return;
    try {
      const { expandedIds, sortCol: sc, sortDir: sd, mainTab: mt } = JSON.parse(raw);
      setExpanded(new Set(expandedIds ?? []));
      if (sc) setSortCol(sc);
      if (sd) setSortDir(sd);
      if (mt) setMainTab(mt);
    } catch { /* ignore corrupt saved state */ }
    sessionStorage.removeItem(LAYOUT_KEY);
  }, []);

  const { data, isLoading, isFetching, refetch, dataUpdatedAt } = useQuery<AgingReport>({
    queryKey: ['cash-flow-aging'],
    queryFn: () => cashFlowApi.getAging().then(r => r.data),
    staleTime: 5 * 60_000,
  });

  const [snapshotBranch, setSnapshotBranch] = useState<string | undefined>(undefined);

  const { data: snapshotData, isLoading: snapshotLoading, isFetching: snapshotFetching, refetch: refetchSnapshot, dataUpdatedAt: snapshotUpdatedAt } = useQuery<SnapshotReport>({
    queryKey: ['cash-flow-snapshot', snapshotBranch],
    queryFn: () => cashFlowApi.getSnapshot(snapshotBranch).then(r => r.data),
    staleTime: 60_000,
    enabled: mainTab === 'snapshot',
  });

  // Auto-select the first available branch returned by the server when none is chosen yet.
  useEffect(() => {
    if (snapshotBranch !== undefined) return;
    const firstBranch = snapshotData?.selectedBranch ?? snapshotData?.branches?.[0]?.branchId;
    if (firstBranch) setSnapshotBranch(firstBranch);
  }, [snapshotData, snapshotBranch]);

  const [captureError, setCaptureError] = useState<string | null>(null);
  const [isCapturing, setIsCapturing] = useState(false);
  const [captureStep, setCaptureStep] = useState<0 | 1 | 2 | 3 | 4>(0);

  // Keep the Acumatica session alive while a capture job is running.
  useEffect(() => {
    if (!isCapturing) return;
    registerBackgroundJob();
    return () => unregisterBackgroundJob();
  }, [isCapturing]);
  const captureStepTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const captureBaseCapturedAt = useRef<string | null | undefined>(undefined);
  const [isExportingForecast, setIsExportingForecast] = useState(false);

  interface CaptureProgress { phase: string; totalVendors: number; completedVendors: number; passLabel: string; passTotal: number; passCompleted: number; startedAt: string | null; }
  const [captureProgress, setCaptureProgress] = useState<CaptureProgress | null>(null);

  const handleExportForecast = async () => {
    setIsExportingForecast(true);
    try {
      const res = await cashFlowApi.exportForecastReport();
      const url = URL.createObjectURL(res.data as Blob);
      const link = document.createElement('a');
      link.href = url;
      link.download = `cashflow-forecast-${new Date().toISOString().slice(0, 10)}.xlsx`;
      document.body.appendChild(link);
      link.click();
      document.body.removeChild(link);
      URL.revokeObjectURL(url);
    } catch {
      setCaptureError('Forecast export failed. Please try again.');
    } finally {
      setIsExportingForecast(false);
    }
  };

  // Poll progress + snapshot every 2 s while a capture job is running.
  useEffect(() => {
    if (!isCapturing) return;
    const interval = setInterval(async () => {
      // Poll progress endpoint for sub-step detail
      let jobDone = false;
      try {
        const prog = await cashFlowApi.getCaptureProgress();
        const p = prog.data as { phase: string; totalVendors: number; completedVendors: number; passLabel: string; passTotal: number; passCompleted: number; startedAt: string | null };
        setCaptureProgress(p);
        // Sync the outer step with the backend phase
        if (p.phase === 'FetchingVendorAging' || p.phase === 'FetchingOpenBills' || p.phase === 'Saving') setCaptureStep(3);
        // Backend reports Done → treat as complete regardless of capturedAt
        if (p.phase === 'Done') jobDone = true;
      } catch { /* ignore */ }

      // Poll snapshot to detect completion (capturedAt changed OR backend phase is Done)
      const result = await refetchSnapshot();
      const newCapturedAt = (result.data as SnapshotReport | undefined)?.capturedAt;
      const capturedAtChanged = !!newCapturedAt && newCapturedAt !== captureBaseCapturedAt.current;
      if (capturedAtChanged || jobDone) {
        setIsCapturing(false);
        setCaptureProgress(null);
        setCaptureStep(4);
        refetch();
        if (captureStepTimer.current) clearTimeout(captureStepTimer.current);
        captureStepTimer.current = setTimeout(() => setCaptureStep(0), 1800);
      }
    }, 2000);
    return () => clearInterval(interval);
  }, [isCapturing, refetchSnapshot, refetch]);

  const refreshSnapshotMutation = useMutation({
    mutationFn: () => { setCaptureStep(1); return cashFlowApi.refreshSnapshot(); },
    onSuccess: () => {
      setCaptureError(null);
      // Record current capturedAt so polling can detect when the new snapshot arrives.
      captureBaseCapturedAt.current = snapshotData?.capturedAt;
      setCaptureStep(2);
      setIsCapturing(true);
      if (captureStepTimer.current) clearTimeout(captureStepTimer.current);
      captureStepTimer.current = setTimeout(() => setCaptureStep(3), 3000);
    },
    onError: (err: unknown) => {
      const msg = (err as { response?: { data?: { message?: string } }; message?: string })
        ?.response?.data?.message
        ?? (err as { message?: string })?.message
        ?? 'Capture failed. Please try again.';
      setCaptureError(msg);
      setCaptureStep(0);
    },
  });

  // Auto-capture when snapshot tab is first opened and today's snapshot is missing.
  // Uses sessionStorage keyed to today's date so it survives component remounts
  // (e.g. navigating away and back) within the same browser session.
  useEffect(() => {
    if (mainTab !== 'snapshot') return;
    if (snapshotLoading || isCapturing) return;
    const today = new Date().toISOString().slice(0, 10);
    const snapshotIsToday = snapshotData?.snapshotDate?.slice(0, 10) === today;
    if (snapshotIsToday) return;
    const storageKey = `autoCaptureFired_${today}`;
    if (sessionStorage.getItem(storageKey)) return;
    sessionStorage.setItem(storageKey, '1');
    setCaptureError(null);
    refreshSnapshotMutation.mutate();
  }, [mainTab, snapshotLoading, snapshotData, isCapturing]);

  // Restore scroll position once data is loaded
  useEffect(() => {
    if (!data || scrollRestored.current) return;
    const raw = sessionStorage.getItem(LAYOUT_KEY + '-scroll');
    if (!raw) return;
    const scrollY = parseInt(raw, 10);
    sessionStorage.removeItem(LAYOUT_KEY + '-scroll');
    requestAnimationFrame(() => window.scrollTo({ top: scrollY, behavior: 'instant' }));
    scrollRestored.current = true;
  }, [data]);

  // Save layout + scroll and navigate to a document
  const goToDocument = (docId: string, e: React.MouseEvent) => {
    e.preventDefault();
    sessionStorage.setItem(LAYOUT_KEY, JSON.stringify({
      expandedIds: [...expanded],
      sortCol, sortDir, mainTab,
    }));
    sessionStorage.setItem(LAYOUT_KEY + '-scroll', String(window.scrollY));
    navigate(`/documents/${docId}`);
  };

  const toggleExpand = (id: string) =>
    setExpanded(prev => {
      const next = new Set(prev);
      next.has(id) ? next.delete(id) : next.add(id);
      return next;
    });

  const toggleSort = (col: typeof sortCol) => {
    if (sortCol === col) setSortDir(d => d === 'asc' ? 'desc' : 'asc');
    else { setSortCol(col); setSortDir('asc'); }
  };

  const sorted = [...(data?.vendors ?? [])].sort((a, b) => {
    let diff = 0;
    if (sortCol === 'vendor')       diff = a.vendorName.localeCompare(b.vendorName);
    else if (sortCol === 'current') diff = (a.erp.current ?? 0)      - (b.erp.current ?? 0);
    else if (sortCol === '30')      diff = (a.erp.aging30 ?? 0)      - (b.erp.aging30 ?? 0);
    else if (sortCol === '60')      diff = (a.erp.aging60 ?? 0)      - (b.erp.aging60 ?? 0);
    else if (sortCol === '90')      diff = (a.erp.aging90 ?? 0)      - (b.erp.aging90 ?? 0);
    else if (sortCol === '91plus')  diff = (a.erp.aging90Plus ?? 0)  - (b.erp.aging90Plus ?? 0);
    else if (sortCol === 'total')   diff = (a.erp.totalOutstanding ?? 0) - (b.erp.totalOutstanding ?? 0);
    return sortDir === 'asc' ? diff : -diff;
  });

  const summary = data?.summary;
  const lastUpdated = dataUpdatedAt ? new Date(dataUpdatedAt).toLocaleTimeString() : null;

  const SortIcon = ({ col }: { col: typeof sortCol }) =>
    sortCol === col
      ? <ChevronDown className={`inline h-3 w-3 ml-0.5 transition-transform ${sortDir === 'desc' ? '' : 'rotate-180'}`} />
      : <ChevronDown className="inline h-3 w-3 ml-0.5 text-muted-foreground/40" />;

  const handleExport = async () => {
    setExporting(true);
    try {
      const res = await cashFlowApi.exportAgingReport(reportDate);
      const url = URL.createObjectURL(res.data as Blob);
      const link = document.createElement('a');
      link.href = url;
      link.download = `ap-aging-${reportDate}.xlsx`;
      document.body.appendChild(link);
      link.click();
      document.body.removeChild(link);
      URL.revokeObjectURL(url);
    } finally {
      setExporting(false);
    }
  };

  return (
    <div className="space-y-5">
      {/* Header */}
      <div className="flex items-center justify-between flex-wrap gap-2">
        <div>
          <h1 className="text-xl sm:text-2xl font-bold text-foreground">Cash Flow</h1>
          <p className="text-sm text-muted-foreground mt-0.5">
            AP aging across all vendors with recorded statements
          </p>
        </div>
        <div className="flex items-center gap-2 flex-wrap">
          {/* Excel export */}
          <div className="flex items-center gap-1.5">
            <input
              type="date"
              value={reportDate}
              onChange={e => setReportDate(e.target.value)}
              className="input text-sm h-8 px-2"
            />
            <button
              onClick={handleExport}
              disabled={exporting}
              className="btn-secondary flex items-center gap-1.5 text-sm"
              title="Export aging report to Excel"
            >
              {exporting
                ? <Loader2 className="h-3.5 w-3.5 animate-spin" />
                : <Download className="h-3.5 w-3.5" />}
              Export
            </button>
          </div>
          {mainTab === 'live' && (
            <>
              {lastUpdated && (
                <span className="text-xs text-muted-foreground">Updated {lastUpdated}</span>
              )}
              <button
                onClick={() => refetch()}
                disabled={isFetching}
                className="btn-secondary flex items-center gap-1.5 text-sm"
              >
                <RefreshCw className={`h-3.5 w-3.5 ${isFetching ? 'animate-spin' : ''}`} />
                Refresh
              </button>
            </>
          )}
        </div>
      </div>

      {/* ── Tabs ── */}
      <div className="flex rounded-lg border border-border overflow-hidden text-sm w-fit">
        <button
          onClick={() => setMainTab('live')}
          className={`px-4 py-1.5 transition-colors ${mainTab === 'live' ? 'bg-primary text-primary-foreground' : 'text-muted-foreground hover:bg-muted'}`}
        >
          Statement vs ERP
        </button>
        <button
          onClick={() => setMainTab('snapshot')}
          className={`px-4 py-1.5 transition-colors ${mainTab === 'snapshot' ? 'bg-primary text-primary-foreground' : 'text-muted-foreground hover:bg-muted'}`}
        >
          Current Aging
        </button>
      </div>

      {/* ══ LIVE TAB ══════════════════════════════════════════════════════════ */}
      {mainTab === 'live' && (
        <>
          {/* Loading */}
          {isLoading && (
            <div className="flex items-center justify-center py-20 gap-2 text-muted-foreground">
              <Loader2 className="h-5 w-5 animate-spin" />
              <span className="text-sm">Loading from last snapshot…</span>
            </div>
          )}

          {!isLoading && data && (
            <>
              {/* ── Summary cards ─────────────────────────────────────────── */}
              {summary?.snapshotMissing && (
                <div className="flex items-start gap-2 text-sm text-amber-700 bg-amber-50 border border-amber-200 rounded-lg px-3 py-2">
                  <Info className="h-4 w-4 text-amber-500 mt-0.5 flex-shrink-0" />
                  <span>No snapshot captured yet. Click <strong>Refresh</strong> to capture aging data from ERP.</span>
                </div>
              )}
              <div className="grid grid-cols-2 sm:grid-cols-3 lg:grid-cols-6 gap-3">
                <SummaryCard
                  label="Total Outstanding"
                  value={summary?.totalOutstanding ?? 0}
                  icon={TrendingUp}
                  iconClass="text-primary"
                  highlight
                />
                <SummaryCard
                  label="Current (Not Due)"
                  value={summary?.totalCurrent ?? 0}
                  icon={CheckCircle}
                  iconClass="text-green-600"
                  barColor="bg-green-100"
                />
                <SummaryCard
                  label="1–30 Days"
                  value={summary?.totalAging30 ?? 0}
                  icon={Clock}
                  iconClass="text-yellow-600"
                  barColor="bg-yellow-100"
                />
                <SummaryCard
                  label="31–60 Days"
                  value={summary?.totalAging60 ?? 0}
                  icon={AlertTriangle}
                  iconClass="text-orange-500"
                  barColor="bg-orange-100"
                />
                <SummaryCard
                  label="61–90 Days"
                  value={summary?.totalAging90 ?? 0}
                  icon={AlertTriangle}
                  iconClass="text-orange-700"
                  barColor="bg-orange-200"
                />
                <SummaryCard
                  label="91+ Days"
                  value={summary?.totalAging90Plus ?? 0}
                  icon={AlertTriangle}
                  iconClass="text-red-500"
                  barColor="bg-red-100"
                />
              </div>

              {/* Portfolio stacked bar */}
              {(summary?.totalOutstanding ?? 0) > 0 && (
                <div className="card p-3 sm:p-4">
                  <div className="flex items-center justify-between mb-2">
                    <span className="text-xs font-medium text-muted-foreground uppercase tracking-wide">
                      Portfolio aging distribution — {summary!.vendorCount} vendors
                    </span>
                    <div className="flex items-center gap-3 text-[11px] text-muted-foreground flex-wrap">
                      <span className="flex items-center gap-1"><span className="w-2.5 h-2.5 rounded-sm bg-green-500 inline-block" />Current</span>
                      <span className="flex items-center gap-1"><span className="w-2.5 h-2.5 rounded-sm bg-yellow-400 inline-block" />1–30d</span>
                      <span className="flex items-center gap-1"><span className="w-2.5 h-2.5 rounded-sm bg-orange-400 inline-block" />31–60d</span>
                      <span className="flex items-center gap-1"><span className="w-2.5 h-2.5 rounded-sm bg-orange-600 inline-block" />61–90d</span>
                      <span className="flex items-center gap-1"><span className="w-2.5 h-2.5 rounded-sm bg-red-500 inline-block" />91+d</span>
                    </div>
                  </div>
                  <div className="flex h-3 w-full rounded-full overflow-hidden gap-px">
                    {(summary!.totalCurrent     > 0) && <div className="bg-green-500"  style={{ width: `${(summary!.totalCurrent     / summary!.totalOutstanding) * 100}%` }} />}
                    {(summary!.totalAging30     > 0) && <div className="bg-yellow-400" style={{ width: `${(summary!.totalAging30     / summary!.totalOutstanding) * 100}%` }} />}
                    {(summary!.totalAging60     > 0) && <div className="bg-orange-400" style={{ width: `${(summary!.totalAging60     / summary!.totalOutstanding) * 100}%` }} />}
                    {(summary!.totalAging90     > 0) && <div className="bg-orange-600" style={{ width: `${(summary!.totalAging90     / summary!.totalOutstanding) * 100}%` }} />}
                    {(summary!.totalAging90Plus > 0) && <div className="bg-red-500"    style={{ width: `${(summary!.totalAging90Plus  / summary!.totalOutstanding) * 100}%` }} />}
                  </div>
                  <div className="flex justify-between mt-1.5 text-[11px] text-muted-foreground">
                    <span>{fmt(summary!.totalCurrent)}</span>
                    <span>{fmt(summary!.totalAging30)}</span>
                    <span>{fmt(summary!.totalAging60)}</span>
                    <span className="text-orange-700 font-medium">{fmt(summary!.totalAging90)}</span>
                    <span className="text-red-500 font-medium">{fmt(summary!.totalAging90Plus)}</span>
                  </div>
                </div>
              )}

              {/* ── Data source note ──────────────────────────────────────── */}
              <div className="flex items-start gap-2 text-sm text-muted-foreground bg-blue-50 border border-blue-100 rounded-lg px-3 py-2 max-w-2xl">
                <Info className="h-4 w-4 text-blue-500 mt-0.5 flex-shrink-0" />
                <span>
                  <strong className="text-blue-700">ERP @ Stmt Date</strong> shows Acumatica AP aging as of each vendor's latest statement date.{' '}
                  Expand a vendor row to compare with the <strong className="text-blue-700">Statement</strong> values extracted from OCR.
                </span>
              </div>

              {/* ── Vendor aging table (desktop) ──────────────────────────── */}
              {sorted.length === 0 ? (
                <div className="card p-8 text-center text-muted-foreground text-sm">
                  No processed vendor statements found. Upload and approve vendor statements to see aging data.
                </div>
              ) : (
                <>
                  <div className="card hidden md:block overflow-x-auto">
                    <table className="w-full text-sm" style={{ tableLayout: 'fixed' }}>
                      <colgroup>
                        <col style={{ width: '4%' }} />
                        <col style={{ width: '20%' }} />
                        <col style={{ width: '7%' }} />
                        <col style={{ width: '11%' }} />
                        <col style={{ width: '11%' }} />
                        <col style={{ width: '11%' }} />
                        <col style={{ width: '11%' }} />
                        <col style={{ width: '11%' }} />
                        <col style={{ width: '14%' }} />
                      </colgroup>
                      <thead className="bg-muted/50 border-b border-border">
                        <tr>
                          <th className="px-3 py-3" />
                          <th className="px-3 py-3 text-left">
                            <button className="font-medium text-muted-foreground hover:text-foreground" onClick={() => toggleSort('vendor')}>
                              Vendor <SortIcon col="vendor" />
                            </button>
                          </th>
                          <th className="px-3 py-3 text-left font-medium text-muted-foreground">Terms</th>
                          <th className="px-3 py-3 text-right">
                            <button className="font-medium text-green-700 hover:text-green-800 w-full text-right" onClick={() => toggleSort('current')}>
                              Current <SortIcon col="current" />
                            </button>
                          </th>
                          <th className="px-3 py-3 text-right">
                            <button className="font-medium text-yellow-700 hover:text-yellow-800 w-full text-right" onClick={() => toggleSort('30')}>
                              1–30 Days <SortIcon col="30" />
                            </button>
                          </th>
                          <th className="px-3 py-3 text-right">
                            <button className="font-medium text-orange-600 hover:text-orange-700 w-full text-right" onClick={() => toggleSort('60')}>
                              31–60 Days <SortIcon col="60" />
                            </button>
                          </th>
                          <th className="px-3 py-3 text-right">
                            <button className="font-medium text-orange-700 hover:text-orange-800 w-full text-right" onClick={() => toggleSort('90')}>
                              61–90 Days <SortIcon col="90" />
                            </button>
                          </th>
                          <th className="px-3 py-3 text-right">
                            <button className="font-medium text-red-600 hover:text-red-700 w-full text-right" onClick={() => toggleSort('91plus')}>
                              91+ Days <SortIcon col="91plus" />
                            </button>
                          </th>
                          <th className="px-3 py-3 text-right">
                            <button className="font-medium text-muted-foreground hover:text-foreground w-full text-right" onClick={() => toggleSort('total')}>
                              Total <SortIcon col="total" />
                            </button>
                          </th>
                        </tr>
                      </thead>
                      <tbody className="divide-y divide-border">
                        {sorted.map(v => {
                          const isOpen = expanded.has(v.vendorLocalId);
                          const stmt = v.statement;

                          // Always show ERP @ statement date values
                          const cur   = v.erp.current;
                          const a30   = v.erp.aging30;
                          const a60   = v.erp.aging60;
                          const a90   = v.erp.aging90;
                          const a91p  = v.erp.aging90Plus;
                          const total = v.erp.totalOutstanding;

                          return (
                            <>
                              <tr
                                key={v.vendorLocalId}
                                className={`hover:bg-muted/30 cursor-pointer ${isOpen ? 'bg-muted/20' : ''}`}
                                onClick={() => toggleExpand(v.vendorLocalId)}
                              >
                                <td className="px-3 py-3 text-muted-foreground">
                                  {isOpen
                                    ? <ChevronDown className="h-4 w-4" />
                                    : <ChevronRight className="h-4 w-4" />}
                                </td>
                                <td className="px-3 py-3">
                                  <div className="font-medium text-foreground truncate">{v.vendorName}</div>
                                  <AgingBar erp={v.erp} total={v.erp.totalOutstanding} />
                                </td>
                                <td className="px-3 py-3 text-muted-foreground text-xs">{v.paymentTerms ?? '—'}</td>
                                <td className={`px-3 py-3 text-right tabular-nums ${amtClass(cur, 'current')}`}>{fmt(cur)}</td>
                                <td className={`px-3 py-3 text-right tabular-nums ${amtClass(a30, '30')}`}>{fmt(a30)}</td>
                                <td className={`px-3 py-3 text-right tabular-nums ${amtClass(a60, '60')}`}>{fmt(a60)}</td>
                                <td className={`px-3 py-3 text-right tabular-nums ${amtClass(a90, '90')}`}>{fmt(a90)}</td>
                                <td className={`px-3 py-3 text-right tabular-nums ${amtClass(a91p, '91plus')}`}>{fmt(a91p)}</td>
                                <td className="px-3 py-3 text-right tabular-nums font-semibold text-foreground">{fmt(total)}</td>
                              </tr>

                              {/* Expanded comparison row */}
                              {isOpen && (
                                <tr key={`${v.vendorLocalId}-exp`} className="bg-muted/10">
                                  <td />
                                  <td colSpan={8} className="px-3 pb-4 pt-2">
                                    <div className="grid grid-cols-2 gap-4">
                                      {/* ERP @ Statement Date side */}
                                      <div className="space-y-1">
                                        <p className="text-xs font-semibold text-blue-700 uppercase tracking-wide mb-2">
                                          ERP @ Stmt Date
                                          {v.erp.snapshotDate && (
                                            <span className="ml-1 font-normal normal-case text-blue-500">
                                              ({new Date(v.erp.snapshotDate).toLocaleDateString('en-MY', { day: '2-digit', month: 'short', year: 'numeric' })})
                                            </span>
                                          )}
                                        </p>
                                        <CompareRow label="Current"     value={v.erp.current}      color="text-green-700" />
                                        <CompareRow label="1–30 Days"   value={v.erp.aging30}      color="text-yellow-700" />
                                        <CompareRow label="31–60 Days"  value={v.erp.aging60}      color="text-orange-600" />
                                        <CompareRow label="61–90 Days"  value={v.erp.aging90}      color="text-orange-700" />
                                        <CompareRow label="91+ Days"    value={v.erp.aging90Plus}  color="text-red-600" />
                                        <div className="border-t border-border pt-1 mt-1">
                                          <CompareRow label="Total Outstanding" value={v.erp.totalOutstanding} bold />
                                        </div>
                                      </div>

                                      {/* Statement (OCR) side */}
                                      <div className="space-y-1">
                                        <div className="flex items-center justify-between gap-2 mb-2 flex-wrap">
                                          <div className="flex items-center gap-2">
                                            <p className="text-xs font-semibold text-foreground uppercase tracking-wide">Last Statement</p>
                                            {statusBadge(stmt.documentStatus)}
                                          </div>
                                          {stmt.statementDate && (
                                            <span className="text-xs font-semibold text-primary">
                                              {new Date(stmt.statementDate).toLocaleDateString('en-MY', { day: '2-digit', month: 'short', year: 'numeric' })}
                                            </span>
                                          )}
                                        </div>
                                        <CompareRow label="Current"     value={stmt.current}           color="text-green-700" />
                                        <CompareRow label="1–30 Days"   value={stmt.aging30}           color="text-yellow-700" />
                                        <CompareRow label="31–60 Days"  value={stmt.aging60}           color="text-orange-600" />
                                        <CompareRow label="61–90 Days"  value={stmt.aging90}           color="text-orange-700" />
                                        <CompareRow label="91+ Days"    value={stmt.aging90Plus}       color="text-red-600" />
                                        <div className="border-t border-border pt-1 mt-1">
                                          <CompareRow label="Outstanding Balance" value={stmt.outstandingBalance} bold />
                                        </div>
                                        <a
                                          href={`/documents/${stmt.documentId}`}
                                          onClick={e => { e.stopPropagation(); goToDocument(stmt.documentId, e); }}
                                          className="inline-flex items-center gap-1 text-[11px] text-primary hover:underline mt-1"
                                        >
                                          <FileText className="h-3 w-3" /> View document
                                        </a>
                                      </div>
                                    </div>
                                  </td>
                                </tr>
                              )}
                            </>
                          );
                        })}

                        {/* Footer totals row */}
                        <tr className="bg-muted/40 border-t-2 border-border font-semibold">
                          <td />
                          <td className="px-3 py-3 text-sm text-foreground">
                            Total ({summary!.vendorCount} vendors)
                          </td>
                          <td />
                          <td className="px-3 py-3 text-right tabular-nums text-green-700">{fmt(summary!.totalCurrent)}</td>
                          <td className="px-3 py-3 text-right tabular-nums text-yellow-700">{fmt(summary!.totalAging30)}</td>
                          <td className="px-3 py-3 text-right tabular-nums text-orange-600">{fmt(summary!.totalAging60)}</td>
                          <td className="px-3 py-3 text-right tabular-nums text-orange-700">{fmt(summary!.totalAging90)}</td>
                          <td className="px-3 py-3 text-right tabular-nums text-red-600">{fmt(summary!.totalAging90Plus)}</td>
                          <td className="px-3 py-3 text-right tabular-nums text-foreground">{fmt(summary!.totalOutstanding)}</td>
                        </tr>
                      </tbody>
                    </table>
                  </div>

                  {/* ── Mobile cards ──────────────────────────────────────── */}
                  <div className="md:hidden space-y-2">
                    {sorted.map(v => {
                      const isOpen = expanded.has(v.vendorLocalId);
                      const stmt   = v.statement;
                      return (
                        <div key={v.vendorLocalId} className="card overflow-hidden">
                          <button
                            className="w-full text-left p-3"
                            onClick={() => toggleExpand(v.vendorLocalId)}
                          >
                            <div className="flex items-start justify-between gap-2">
                              <div className="flex-1 min-w-0">
                                <div className="flex items-center gap-1.5">
                                  <Building2 className="h-3.5 w-3.5 text-muted-foreground flex-shrink-0" />
                                  <span className="font-medium text-foreground text-sm truncate">{v.vendorName}</span>
                                </div>
                                {v.paymentTerms && (
                                  <span className="text-xs text-muted-foreground">{v.paymentTerms}</span>
                                )}
                              </div>
                              <div className="text-right flex-shrink-0">
                                <div className="text-sm font-semibold text-foreground">{fmtShort(v.erp.totalOutstanding)}</div>
                                <div className="text-[10px] text-muted-foreground">outstanding</div>
                              </div>
                            </div>
                            <AgingBar erp={v.erp} total={v.erp.totalOutstanding} />
                            <div className="flex gap-3 mt-1.5 text-xs flex-wrap">
                              <span className="text-green-700">{fmtShort(v.erp.current)} cur</span>
                              <span className="text-yellow-700">{fmtShort(v.erp.aging30)} 30d</span>
                              <span className="text-orange-600">{fmtShort(v.erp.aging60)} 60d</span>
                              <span className="text-orange-700 font-semibold">{fmtShort(v.erp.aging90)} 90d</span>
                              <span className="text-red-600 font-semibold">{fmtShort(v.erp.aging90Plus)} 91+</span>
                            </div>
                          </button>

                          {isOpen && (
                            <div className="border-t border-border px-3 pb-3 pt-2 space-y-3 bg-muted/10">
                              <div>
                                <p className="text-[11px] font-semibold text-blue-700 uppercase tracking-wide mb-1">ERP @ Stmt Date</p>
                                <div className="grid grid-cols-2 gap-x-4 gap-y-0.5 text-xs">
                                  <CompareRow label="Current"     value={v.erp.current}      color="text-green-700" />
                                  <CompareRow label="1–30 Days"   value={v.erp.aging30}      color="text-yellow-700" />
                                  <CompareRow label="31–60 Days"  value={v.erp.aging60}      color="text-orange-600" />
                                  <CompareRow label="61–90 Days"  value={v.erp.aging90}      color="text-orange-700" />
                                  <CompareRow label="91+ Days"    value={v.erp.aging90Plus}  color="text-red-600" />
                                  <CompareRow label="Total"       value={v.erp.totalOutstanding} bold />
                                </div>
                              </div>
                              <div>
                                <div className="flex items-center gap-1.5 mb-1">
                                  <p className="text-[11px] font-semibold text-foreground uppercase tracking-wide">Statement</p>
                                  {statusBadge(stmt.documentStatus)}
                                  {stmt.statementDate && (
                                    <span className="text-[10px] text-muted-foreground">
                                      {new Date(stmt.statementDate).toLocaleDateString('en-MY', { day: '2-digit', month: 'short', year: 'numeric' })}
                                    </span>
                                  )}
                                </div>
                                <div className="grid grid-cols-2 gap-x-4 gap-y-0.5 text-xs">
                                  <CompareRow label="Current"     value={stmt.current}            color="text-green-700" />
                                  <CompareRow label="1–30 Days"   value={stmt.aging30}            color="text-yellow-700" />
                                  <CompareRow label="31–60 Days"  value={stmt.aging60}            color="text-orange-600" />
                                  <CompareRow label="61–90 Days"  value={stmt.aging90}            color="text-orange-700" />
                                  <CompareRow label="91+ Days"    value={stmt.aging90Plus}        color="text-red-600" />
                                  <CompareRow label="Balance"     value={stmt.outstandingBalance} bold />
                                </div>
                                <a
                                  href={`/documents/${stmt.documentId}`}
                                  onClick={e => goToDocument(stmt.documentId, e)}
                                  className="inline-flex items-center gap-1 text-[11px] text-primary hover:underline mt-1.5"
                                >
                                  <FileText className="h-3 w-3" /> View document
                                </a>
                              </div>
                            </div>
                          )}
                        </div>
                      );
                    })}
                  </div>
                </>
              )}

              {/* ── MANAGER section ───────────────────────────────────────── */}
              <ManagerSection vendors={sorted} goToDocument={goToDocument} />
            </>
          )}
        </>
      )}

      {/* ══ SNAPSHOT TAB ══════════════════════════════════════════════════════ */}
      {mainTab === 'snapshot' && (
        <>
          {captureError && (
            <div className="mb-3 flex items-start gap-2 rounded-md border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-700">
              <AlertTriangle className="mt-0.5 h-4 w-4 shrink-0" />
              <span>{captureError}</span>
              <button onClick={() => setCaptureError(null)} className="ml-auto text-red-400 hover:text-red-600">✕</button>
            </div>
          )}
          <SnapshotTab
            data={snapshotData}
            isLoading={snapshotLoading}
            isFetching={snapshotFetching}
            isRefreshing={refreshSnapshotMutation.isPending || isCapturing}
            captureStep={captureStep}
            captureProgress={captureProgress}
            onRefresh={() => { setCaptureError(null); refreshSnapshotMutation.mutate(); }}
            onExportForecast={handleExportForecast}
            isExportingForecast={isExportingForecast}
            updatedAt={snapshotUpdatedAt}
            selectedBranch={snapshotBranch}
            onBranchChange={b => setSnapshotBranch(b)}
          />
        </>
      )}
    </div>
  );
}

// ── Snapshot Tab ───────────────────────────────────────────────────────────

const CAPTURE_STEPS: { label: string; detail: string }[] = [
  { label: 'Fetching from Acumatica', detail: 'Pulling AP aging data from ERP' },
  { label: 'Saving data',             detail: 'Persisting records to database' },
  { label: 'Populating data',         detail: 'Processing and aggregating entries' },
  { label: 'Visualizing data',        detail: 'Rendering updated snapshot' },
];

interface CaptureProgressProp { phase: string; totalVendors: number; completedVendors: number; passLabel: string; passTotal: number; passCompleted: number; startedAt: string | null; }

function SnapshotTab({
  data, isLoading, isFetching, isRefreshing, captureStep, captureProgress, onRefresh, onExportForecast, isExportingForecast, updatedAt, selectedBranch, onBranchChange,
}: {
  data: SnapshotReport | undefined;
  isLoading: boolean;
  isFetching: boolean;
  isRefreshing: boolean;
  captureStep: 0 | 1 | 2 | 3 | 4;
  captureProgress: CaptureProgressProp | null;
  onRefresh: () => void;
  onExportForecast: () => void;
  isExportingForecast: boolean;
  updatedAt: number;
  selectedBranch: string | undefined;
  onBranchChange: (branchId: string) => void;
}) {
  const [sortCol, setSortCol] = useState<'vendor' | 'current' | '30' | '60' | '90' | '91plus' | 'total'>('vendor');
  const [sortDir, setSortDir] = useState<'asc' | 'desc'>('asc');

  const toggleSort = (col: typeof sortCol) => {
    if (sortCol === col) setSortDir(d => d === 'asc' ? 'desc' : 'asc');
    else { setSortCol(col); setSortDir('asc'); }
  };

  const sorted = [...(data?.vendors ?? [])].sort((a, b) => {
    let diff = 0;
    if (sortCol === 'vendor')       diff = a.vendorName.localeCompare(b.vendorName);
    else if (sortCol === 'current') diff = a.current - b.current;
    else if (sortCol === '30')      diff = a.aging30 - b.aging30;
    else if (sortCol === '60')      diff = a.aging60 - b.aging60;
    else if (sortCol === '90')      diff = a.aging90 - b.aging90;
    else if (sortCol === '91plus')  diff = a.aging90Plus - b.aging90Plus;
    else if (sortCol === 'total')   diff = a.totalOutstanding - b.totalOutstanding;
    return sortDir === 'asc' ? diff : -diff;
  });

  const SortIcon = ({ col }: { col: typeof sortCol }) =>
    sortCol === col
      ? <ChevronDown className={`inline h-3 w-3 ml-0.5 transition-transform ${sortDir === 'desc' ? '' : 'rotate-180'}`} />
      : <ChevronDown className="inline h-3 w-3 ml-0.5 text-muted-foreground/40" />;

  const snapshotDateLabel = data?.snapshotDate
    ? new Date(data.snapshotDate).toLocaleDateString('en-MY', { day: '2-digit', month: 'short', year: 'numeric' })
    : null;
  const capturedAtLabel = data?.capturedAt
    ? new Date(data.capturedAt).toLocaleString('en-MY', { day: '2-digit', month: 'short', year: 'numeric', hour: '2-digit', minute: '2-digit' })
    : null;
  const lastFetchedLabel = updatedAt ? new Date(updatedAt).toLocaleTimeString() : null;

  const summary = data?.summary;

  return (
    <div className="space-y-4">
      {/* Snapshot header */}
      <div className="flex items-center justify-between flex-wrap gap-2">
        <div>
          {snapshotDateLabel && (
            <p className="text-sm text-muted-foreground">
              Current AP aging for <strong>{snapshotDateLabel}</strong>
              {capturedAtLabel && <> · captured {capturedAtLabel}</>}
            </p>
          )}
          {!snapshotDateLabel && !isLoading && (
            <p className="text-sm text-muted-foreground">No snapshot available yet.</p>
          )}
        </div>
        <div className="flex items-center gap-2">
          {(data?.branches?.length ?? 0) > 0 && (
            <select
              value={selectedBranch ?? ''}
              onChange={e => onBranchChange(e.target.value)}
              className="input text-sm h-8 px-2"
              title="Filter by branch"
            >
              {data!.branches.map(b => (
                <option key={b.branchId} value={b.branchId}>{b.branchId}</option>
              ))}
            </select>
          )}
          {lastFetchedLabel && (
            <span className="text-xs text-muted-foreground">Loaded {lastFetchedLabel}</span>
          )}
          <button
            onClick={onExportForecast}
            disabled={isExportingForecast || !data?.snapshotDate}
            className="btn-secondary flex items-center gap-1.5 text-sm"
            title="Export cash flow forecast to Excel"
          >
            {isExportingForecast
              ? <Loader2 className="h-3.5 w-3.5 animate-spin" />
              : <Download className="h-3.5 w-3.5" />}
            {isExportingForecast ? 'Exporting…' : 'Export Forecast'}
          </button>
          <button
            onClick={onRefresh}
            disabled={isRefreshing || isFetching}
            className="btn-secondary flex items-center gap-1.5 text-sm"
            title="Re-capture today's aging snapshot from ERP"
          >
            {isRefreshing
              ? <Loader2 className="h-3.5 w-3.5 animate-spin" />
              : <RefreshCw className={`h-3.5 w-3.5 ${isFetching ? 'animate-spin' : ''}`} />}
            {isRefreshing ? 'Capturing…' : 'Capture Now'}
          </button>
        </div>
      </div>

      {/* Capture progress stepper */}
      {captureStep > 0 && (
        <div className="rounded-lg border border-blue-200 bg-blue-50/60 px-4 py-3 space-y-3">
          <div className="flex items-center gap-3 flex-wrap">
            {CAPTURE_STEPS.map((s, idx) => {
              const stepNum = idx + 1 as 1 | 2 | 3 | 4;
              const isDone    = captureStep > stepNum;
              const isActive  = captureStep === stepNum;
              return (
                <div key={stepNum} className="flex items-center gap-1.5 min-w-0">
                  {idx > 0 && (
                    <div className={`hidden sm:block h-px w-6 flex-shrink-0 transition-colors duration-500 ${isDone || isActive ? 'bg-blue-400' : 'bg-blue-200'}`} />
                  )}
                  <div className={`flex-shrink-0 h-5 w-5 rounded-full flex items-center justify-center transition-all duration-500 ${
                    isDone    ? 'bg-blue-500 text-white'
                    : isActive ? 'bg-blue-500 text-white ring-4 ring-blue-200'
                    : 'bg-white border border-blue-200 text-blue-300'
                  }`}>
                    {isDone ? (
                      <svg className="h-3 w-3" viewBox="0 0 12 12" fill="none" stroke="currentColor" strokeWidth="2.5" strokeLinecap="round" strokeLinejoin="round">
                        <polyline points="2,6 5,9 10,3" />
                      </svg>
                    ) : isActive ? (
                      <Loader2 className="h-3 w-3 animate-spin" />
                    ) : (
                      <span className="text-[9px] font-semibold leading-none">{stepNum}</span>
                    )}
                  </div>
                  <div className="min-w-0">
                    <p className={`text-xs font-medium leading-tight transition-colors duration-300 ${
                      isDone ? 'text-blue-500' : isActive ? 'text-blue-700' : 'text-blue-300'
                    }`}>
                      {s.label}
                    </p>
                    {isActive && stepNum !== 3 && (
                      <p className="text-[10px] text-blue-500 leading-tight mt-0.5">{s.detail}</p>
                    )}
                  </div>
                </div>
              );
            })}
          </div>

          {/* Step 3 detail: real-time vendor progress from backend */}
          {captureStep === 3 && captureProgress && (
            <div className="space-y-1.5 pt-1 border-t border-blue-200">
              {/* Phase label */}
              <div className="flex items-center justify-between text-[11px] text-blue-600">
                <span className="font-medium">
                  {captureProgress.phase === 'Saving'
                    ? 'Saving snapshot records to database…'
                    : captureProgress.passLabel
                    ? captureProgress.passLabel
                    : captureProgress.phase === 'FetchingVendorAging'
                    ? 'Statement-date aging'
                    : captureProgress.phase === 'FetchingOpenBills'
                    ? 'Current aging'
                    : 'Processing…'}
                </span>
                {captureProgress.passTotal > 0 && (
                  <span className="tabular-nums font-semibold">
                    {captureProgress.passCompleted} / {captureProgress.passTotal} vendors
                  </span>
                )}
              </div>
              {/* Per-pass progress bar */}
              {captureProgress.passTotal > 0 && (
                <div className="h-1.5 w-full rounded-full bg-blue-100 overflow-hidden">
                  <div
                    className="h-full rounded-full bg-blue-500 transition-all duration-500"
                    style={{ width: `${Math.round((captureProgress.passCompleted / captureProgress.passTotal) * 100)}%` }}
                  />
                </div>
              )}
            </div>
          )}
        </div>
      )}

      {isLoading && (
        <div className="flex items-center justify-center py-20 gap-2 text-muted-foreground">
          <Loader2 className="h-5 w-5 animate-spin" />
          <span className="text-sm">Loading current AP aging…</span>
        </div>
      )}

      {!isLoading && data && summary && (
        <>
          {/* Summary cards */}
          <div className="grid grid-cols-2 sm:grid-cols-3 lg:grid-cols-6 gap-3">
            <SummaryCard label="Total Outstanding" value={summary.totalOutstanding} icon={TrendingUp}   iconClass="text-primary" highlight />
            <SummaryCard label="Current (Not Due)" value={summary.totalCurrent}     icon={CheckCircle}  iconClass="text-green-600"  barColor="bg-green-100" />
            <SummaryCard label="1–30 Days"          value={summary.totalAging30}     icon={Clock}        iconClass="text-yellow-600" barColor="bg-yellow-100" />
            <SummaryCard label="31–60 Days"         value={summary.totalAging60}     icon={AlertTriangle} iconClass="text-orange-500" barColor="bg-orange-100" />
            <SummaryCard label="61–90 Days"         value={summary.totalAging90}     icon={AlertTriangle} iconClass="text-orange-700" barColor="bg-orange-200" />
            <SummaryCard label="91+ Days"           value={summary.totalAging90Plus} icon={AlertTriangle} iconClass="text-red-500"   barColor="bg-red-100" />
          </div>

          {/* Portfolio bar */}
          {summary.totalOutstanding > 0 && (
            <div className="card p-3 sm:p-4">
              <div className="flex items-center justify-between mb-2">
                <span className="text-xs font-medium text-muted-foreground uppercase tracking-wide">
                  Portfolio aging distribution — {summary.vendorCount} vendors
                </span>
                <div className="flex items-center gap-3 text-[11px] text-muted-foreground flex-wrap">
                  <span className="flex items-center gap-1"><span className="w-2.5 h-2.5 rounded-sm bg-green-500 inline-block" />Current</span>
                  <span className="flex items-center gap-1"><span className="w-2.5 h-2.5 rounded-sm bg-yellow-400 inline-block" />1–30d</span>
                  <span className="flex items-center gap-1"><span className="w-2.5 h-2.5 rounded-sm bg-orange-400 inline-block" />31–60d</span>
                  <span className="flex items-center gap-1"><span className="w-2.5 h-2.5 rounded-sm bg-orange-600 inline-block" />61–90d</span>
                  <span className="flex items-center gap-1"><span className="w-2.5 h-2.5 rounded-sm bg-red-500 inline-block" />91+d</span>
                </div>
              </div>
              <div className="flex h-3 w-full rounded-full overflow-hidden gap-px">
                {summary.totalCurrent     > 0 && <div className="bg-green-500"  style={{ width: `${(summary.totalCurrent     / summary.totalOutstanding) * 100}%` }} />}
                {summary.totalAging30     > 0 && <div className="bg-yellow-400" style={{ width: `${(summary.totalAging30     / summary.totalOutstanding) * 100}%` }} />}
                {summary.totalAging60     > 0 && <div className="bg-orange-400" style={{ width: `${(summary.totalAging60     / summary.totalOutstanding) * 100}%` }} />}
                {summary.totalAging90     > 0 && <div className="bg-orange-600" style={{ width: `${(summary.totalAging90     / summary.totalOutstanding) * 100}%` }} />}
                {summary.totalAging90Plus > 0 && <div className="bg-red-500"    style={{ width: `${(summary.totalAging90Plus  / summary.totalOutstanding) * 100}%` }} />}
              </div>
            </div>
          )}

          {/* Vendor table — desktop */}
          {sorted.length === 0 ? (
            <div className="card p-8 text-center text-muted-foreground text-sm">
              No current AP aging captured yet. Click "Capture Now" to record today's aging from ERP.
            </div>
          ) : (
            <>
              <div className="card hidden md:block overflow-x-auto">
                <table className="w-full text-sm" style={{ tableLayout: 'fixed' }}>
                  <colgroup>
                    <col style={{ width: '22%' }} />
                    <col style={{ width: '13%' }} />
                    <col style={{ width: '13%' }} />
                    <col style={{ width: '13%' }} />
                    <col style={{ width: '13%' }} />
                    <col style={{ width: '13%' }} />
                    <col style={{ width: '13%' }} />
                  </colgroup>
                  <thead className="bg-muted/50 border-b border-border">
                    <tr>
                      <th className="px-3 py-3 text-left">
                        <button className="font-medium text-muted-foreground hover:text-foreground" onClick={() => toggleSort('vendor')}>
                          Vendor <SortIcon col="vendor" />
                        </button>
                      </th>
                      <th className="px-3 py-3 text-right">
                        <button className="font-medium text-green-700 hover:text-green-800 w-full text-right" onClick={() => toggleSort('current')}>
                          Current <SortIcon col="current" />
                        </button>
                      </th>
                      <th className="px-3 py-3 text-right">
                        <button className="font-medium text-yellow-700 hover:text-yellow-800 w-full text-right" onClick={() => toggleSort('30')}>
                          1–30 Days <SortIcon col="30" />
                        </button>
                      </th>
                      <th className="px-3 py-3 text-right">
                        <button className="font-medium text-orange-600 hover:text-orange-700 w-full text-right" onClick={() => toggleSort('60')}>
                          31–60 Days <SortIcon col="60" />
                        </button>
                      </th>
                      <th className="px-3 py-3 text-right">
                        <button className="font-medium text-orange-700 hover:text-orange-800 w-full text-right" onClick={() => toggleSort('90')}>
                          61–90 Days <SortIcon col="90" />
                        </button>
                      </th>
                      <th className="px-3 py-3 text-right">
                        <button className="font-medium text-red-600 hover:text-red-700 w-full text-right" onClick={() => toggleSort('91plus')}>
                          91+ Days <SortIcon col="91plus" />
                        </button>
                      </th>
                      <th className="px-3 py-3 text-right">
                        <button className="font-medium text-muted-foreground hover:text-foreground w-full text-right" onClick={() => toggleSort('total')}>
                          Total <SortIcon col="total" />
                        </button>
                      </th>
                    </tr>
                  </thead>
                  <tbody className="divide-y divide-border">
                    {sorted.map(v => (
                      <tr key={v.vendorLocalId} className="hover:bg-muted/30">
                        <td className="px-3 py-3">
                          <div className="font-medium text-foreground truncate">{v.vendorName}</div>
                          <AgingBarFlat current={v.current} aging30={v.aging30} aging60={v.aging60} aging90={v.aging90} aging90Plus={v.aging90Plus} total={v.totalOutstanding} />
                        </td>
                        <td className={`px-3 py-3 text-right tabular-nums ${amtClass(v.current, 'current')}`}>{fmt(v.current)}</td>
                        <td className={`px-3 py-3 text-right tabular-nums ${amtClass(v.aging30, '30')}`}>{fmt(v.aging30)}</td>
                        <td className={`px-3 py-3 text-right tabular-nums ${amtClass(v.aging60, '60')}`}>{fmt(v.aging60)}</td>
                        <td className={`px-3 py-3 text-right tabular-nums ${amtClass(v.aging90, '90')}`}>{fmt(v.aging90)}</td>
                        <td className={`px-3 py-3 text-right tabular-nums ${amtClass(v.aging90Plus, '91plus')}`}>{fmt(v.aging90Plus)}</td>
                        <td className="px-3 py-3 text-right tabular-nums font-semibold text-foreground">{fmt(v.totalOutstanding)}</td>
                      </tr>
                    ))}
                    {/* Totals */}
                    <tr className="bg-muted/40 border-t-2 border-border font-semibold">
                      <td className="px-3 py-3 text-sm text-foreground">Total ({summary.vendorCount} vendors)</td>
                      <td className="px-3 py-3 text-right tabular-nums text-green-700">{fmt(summary.totalCurrent)}</td>
                      <td className="px-3 py-3 text-right tabular-nums text-yellow-700">{fmt(summary.totalAging30)}</td>
                      <td className="px-3 py-3 text-right tabular-nums text-orange-600">{fmt(summary.totalAging60)}</td>
                      <td className="px-3 py-3 text-right tabular-nums text-orange-700">{fmt(summary.totalAging90)}</td>
                      <td className="px-3 py-3 text-right tabular-nums text-red-600">{fmt(summary.totalAging90Plus)}</td>
                      <td className="px-3 py-3 text-right tabular-nums text-foreground">{fmt(summary.totalOutstanding)}</td>
                    </tr>
                  </tbody>
                </table>
              </div>

              {/* Mobile cards */}
              <div className="md:hidden space-y-2">
                {sorted.map(v => (
                  <div key={v.vendorLocalId} className="card p-3">
                    <div className="flex items-center justify-between gap-2">
                      <div className="flex items-center gap-1.5 min-w-0">
                        <Building2 className="h-3.5 w-3.5 text-muted-foreground flex-shrink-0" />
                        <span className="font-medium text-sm truncate">{v.vendorName}</span>
                      </div>
                      <span className="text-sm font-semibold text-foreground flex-shrink-0">{fmtShort(v.totalOutstanding)}</span>
                    </div>
                    <AgingBarFlat current={v.current} aging30={v.aging30} aging60={v.aging60} aging90={v.aging90} aging90Plus={v.aging90Plus} total={v.totalOutstanding} />
                    <div className="flex gap-3 mt-1.5 text-xs flex-wrap">
                      <span className="text-green-700">{fmtShort(v.current)} cur</span>
                      <span className="text-yellow-700">{fmtShort(v.aging30)} 30d</span>
                      <span className="text-orange-600">{fmtShort(v.aging60)} 60d</span>
                      <span className="text-orange-700 font-semibold">{fmtShort(v.aging90)} 90d</span>
                      <span className="text-red-600 font-semibold">{fmtShort(v.aging90Plus)} 91+</span>
                    </div>
                  </div>
                ))}
              </div>
            </>
          )}
        </>
      )}
    </div>
  );
}

// ── Manager section ───────────────────────────────────────────────────────

function ManagerSection({ vendors, goToDocument }: { vendors: VendorAgingRow[]; goToDocument: (id: string, e: React.MouseEvent) => void }) {
  const [expandedMgr, setExpandedMgr] = useState<Set<string>>(new Set());

  if (vendors.length === 0) return null;

  // Group vendors by manager
  const byManager = vendors.reduce<Record<string, { name: string; vendors: VendorAgingRow[] }>>(
    (acc, v) => {
      const key = v.managerId;
      if (!acc[key]) acc[key] = { name: v.managerName, vendors: [] };
      acc[key].vendors.push(v);
      return acc;
    },
    {},
  );

  const managers = Object.entries(byManager)
    .map(([id, { name, vendors: vs }]) => ({
      id,
      name,
      vendors: vs,
      totalOutstanding: vs.reduce((s, v) => s + v.erp.totalOutstanding, 0),
      totalCurrent:     vs.reduce((s, v) => s + v.erp.current, 0),
      totalAging30:     vs.reduce((s, v) => s + v.erp.aging30, 0),
      totalAging60:     vs.reduce((s, v) => s + v.erp.aging60, 0),
      totalAging90:     vs.reduce((s, v) => s + v.erp.aging90, 0),
      totalAging90Plus: vs.reduce((s, v) => s + v.erp.aging90Plus, 0),
    }))
    .sort((a, b) => b.totalOutstanding - a.totalOutstanding);

  const toggleMgr = (id: string) =>
    setExpandedMgr(prev => {
      const next = new Set(prev);
      next.has(id) ? next.delete(id) : next.add(id);
      return next;
    });

  const initials = (name: string) =>
    name.split(' ').map(p => p[0]).join('').slice(0, 2).toUpperCase();

  return (
    <div className="space-y-3">
      <div className="flex items-center gap-2">
        <Users className="h-5 w-5 text-muted-foreground" />
        <h2 className="text-base font-semibold text-foreground">Manager</h2>
        <span className="text-xs text-muted-foreground">({managers.length} manager{managers.length !== 1 ? 's' : ''})</span>
      </div>

      <div className="space-y-2">
        {managers.map(mgr => {
          const isOpen = expandedMgr.has(mgr.id);
          return (
            <div key={mgr.id} className="card overflow-hidden">
              {/* Manager header row */}
              <button
                className="w-full text-left hover:bg-muted/30 transition-colors"
                onClick={() => toggleMgr(mgr.id)}
              >
                <div className="px-4 py-3 flex items-center gap-3">
                  {/* Avatar */}
                  <div className="w-8 h-8 rounded-full bg-primary/10 flex items-center justify-center text-xs font-semibold text-primary flex-shrink-0">
                    {initials(mgr.name)}
                  </div>

                  {/* Name + vendor count */}
                  <div className="flex-1 min-w-0">
                    <div className="flex items-center gap-2">
                      <span className="font-medium text-foreground">{mgr.name}</span>
                      <span className="text-xs text-muted-foreground">
                        {mgr.vendors.length} vendor{mgr.vendors.length !== 1 ? 's' : ''}
                      </span>
                    </div>
                    <AgingBar erp={{ current: mgr.totalCurrent, aging30: mgr.totalAging30, aging60: mgr.totalAging60, aging90: mgr.totalAging90, aging90Plus: mgr.totalAging90Plus, totalOutstanding: mgr.totalOutstanding, billCount: 0 }} total={mgr.totalOutstanding} />
                  </div>

                  {/* Aging summary — desktop */}
                  <div className="hidden sm:flex items-center gap-4 text-sm tabular-nums flex-shrink-0">
                    <div className="text-right">
                      <div className={amtClass(mgr.totalCurrent, 'current')}>{fmt(mgr.totalCurrent)}</div>
                      <div className="text-[10px] text-muted-foreground">Current</div>
                    </div>
                    <div className="text-right">
                      <div className={amtClass(mgr.totalAging30, '30')}>{fmt(mgr.totalAging30)}</div>
                      <div className="text-[10px] text-muted-foreground">1–30d</div>
                    </div>
                    <div className="text-right">
                      <div className={amtClass(mgr.totalAging60, '60')}>{fmt(mgr.totalAging60)}</div>
                      <div className="text-[10px] text-muted-foreground">31–60d</div>
                    </div>
                    <div className="text-right">
                      <div className={amtClass(mgr.totalAging90, '90')}>{fmt(mgr.totalAging90)}</div>
                      <div className="text-[10px] text-muted-foreground">61–90d</div>
                    </div>
                    <div className="text-right">
                      <div className={amtClass(mgr.totalAging90Plus, '91plus')}>{fmt(mgr.totalAging90Plus)}</div>
                      <div className="text-[10px] text-muted-foreground">91+d</div>
                    </div>
                    <div className="text-right min-w-[80px]">
                      <div className="font-semibold text-foreground">{fmt(mgr.totalOutstanding)}</div>
                      <div className="text-[10px] text-muted-foreground">Total</div>
                    </div>
                  </div>

                  {/* Chevron */}
                  {isOpen
                    ? <ChevronDown className="h-4 w-4 text-muted-foreground flex-shrink-0" />
                    : <ChevronRight className="h-4 w-4 text-muted-foreground flex-shrink-0" />}
                </div>
              </button>

              {/* Expanded vendor list */}
              {isOpen && (
                <div className="border-t border-border">
                  <table className="w-full text-sm">
                    <thead className="bg-muted/30">
                      <tr>
                        <th className="px-4 py-2 text-left font-medium text-muted-foreground text-xs">Vendor</th>
                        <th className="px-4 py-2 text-left font-medium text-muted-foreground text-xs hidden sm:table-cell">Terms</th>
                        <th className="px-4 py-2 text-right font-medium text-green-700 text-xs">Current</th>
                        <th className="px-4 py-2 text-right font-medium text-yellow-700 text-xs hidden sm:table-cell">1–30d</th>
                        <th className="px-4 py-2 text-right font-medium text-orange-600 text-xs hidden sm:table-cell">31–60d</th>
                        <th className="px-4 py-2 text-right font-medium text-orange-700 text-xs hidden sm:table-cell">61–90d</th>
                        <th className="px-4 py-2 text-right font-medium text-red-600 text-xs">91+d</th>
                        <th className="px-4 py-2 text-right font-medium text-muted-foreground text-xs">Total</th>
                        <th className="px-4 py-2 text-right font-medium text-muted-foreground text-xs">Statement</th>
                      </tr>
                    </thead>
                    <tbody className="divide-y divide-border">
                      {mgr.vendors.map(v => (
                        <tr key={v.vendorLocalId} className="hover:bg-muted/20">
                          <td className="px-4 py-2">
                            <a href={`/documents/${v.statement.documentId}`} onClick={e => goToDocument(v.statement.documentId, e)} className="text-primary hover:underline text-sm font-medium">
                              {v.vendorName}
                            </a>
                          </td>
                          <td className="px-4 py-2 text-muted-foreground text-xs hidden sm:table-cell">{v.paymentTerms ?? '—'}</td>
                          <td className={`px-4 py-2 text-right tabular-nums text-xs ${amtClass(v.erp.current, 'current')}`}>{fmt(v.erp.current)}</td>
                          <td className={`px-4 py-2 text-right tabular-nums text-xs hidden sm:table-cell ${amtClass(v.erp.aging30, '30')}`}>{fmt(v.erp.aging30)}</td>
                          <td className={`px-4 py-2 text-right tabular-nums text-xs hidden sm:table-cell ${amtClass(v.erp.aging60, '60')}`}>{fmt(v.erp.aging60)}</td>
                          <td className={`px-4 py-2 text-right tabular-nums text-xs hidden sm:table-cell ${amtClass(v.erp.aging90, '90')}`}>{fmt(v.erp.aging90)}</td>
                          <td className={`px-4 py-2 text-right tabular-nums text-xs ${amtClass(v.erp.aging90Plus, '91plus')}`}>{fmt(v.erp.aging90Plus)}</td>
                          <td className="px-4 py-2 text-right tabular-nums text-xs font-semibold text-foreground">{fmt(v.erp.totalOutstanding)}</td>
                          <td className="px-4 py-2 text-right">
                            {statusBadge(v.statement.documentStatus)}
                          </td>
                        </tr>
                      ))}
                      {/* Manager subtotal */}
                      <tr className="bg-muted/20 font-medium">
                        <td className="px-4 py-2 text-xs text-muted-foreground" colSpan={2}>Subtotal</td>
                        <td className={`px-4 py-2 text-right tabular-nums text-xs ${amtClass(mgr.totalCurrent, 'current')}`}>{fmt(mgr.totalCurrent)}</td>
                        <td className={`px-4 py-2 text-right tabular-nums text-xs hidden sm:table-cell ${amtClass(mgr.totalAging30, '30')}`}>{fmt(mgr.totalAging30)}</td>
                        <td className={`px-4 py-2 text-right tabular-nums text-xs hidden sm:table-cell ${amtClass(mgr.totalAging60, '60')}`}>{fmt(mgr.totalAging60)}</td>
                        <td className={`px-4 py-2 text-right tabular-nums text-xs hidden sm:table-cell ${amtClass(mgr.totalAging90, '90')}`}>{fmt(mgr.totalAging90)}</td>
                        <td className={`px-4 py-2 text-right tabular-nums text-xs ${amtClass(mgr.totalAging90Plus, '91plus')}`}>{fmt(mgr.totalAging90Plus)}</td>
                        <td className="px-4 py-2 text-right tabular-nums text-xs font-semibold text-foreground">{fmt(mgr.totalOutstanding)}</td>
                        <td />
                      </tr>
                    </tbody>
                  </table>
                </div>
              )}
            </div>
          );
        })}
      </div>
    </div>
  );
}

// ── Sub-components ────────────────────────────────────────────────────────

function SummaryCard({
  label, value, icon: Icon, iconClass, barColor, highlight,
}: {
  label: string;
  value: number;
  icon: React.ElementType;
  iconClass: string;
  barColor?: string;
  highlight?: boolean;
}) {
  return (
    <div className={`card p-3 sm:p-4 ${highlight ? 'border-primary/30' : ''}`}>
      <div className="flex items-center justify-between mb-1.5">
        <span className="text-xs text-muted-foreground">{label}</span>
        <Icon className={`h-4 w-4 ${iconClass}`} />
      </div>
      <div className={`text-lg sm:text-xl font-bold tabular-nums ${highlight ? 'text-primary' : 'text-foreground'}`}>
        {fmtShort(value)}
      </div>
      {barColor && (
        <div className="mt-2 h-1 rounded-full bg-muted overflow-hidden">
          <div className={`h-full rounded-full ${barColor.replace('bg-', 'bg-').replace('100', '500')}`} style={{ width: '100%', opacity: value > 0 ? 1 : 0.15 }} />
        </div>
      )}
    </div>
  );
}

function CompareRow({
  label, value, color = 'text-foreground', bold,
}: {
  label: string;
  value: number | null | undefined;
  color?: string;
  bold?: boolean;
}) {
  return (
    <div className="flex items-center justify-between gap-2 py-0.5">
      <span className="text-muted-foreground text-[11px]">{label}</span>
      <span className={`tabular-nums text-[11px] ${color} ${bold ? 'font-semibold' : ''}`}>
        {fmt(value ?? null)}
      </span>
    </div>
  );
}
