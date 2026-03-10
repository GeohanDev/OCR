import { useState, useEffect } from 'react';
import { erpApi } from '../../api/client';
import { useAuth } from '../../contexts/AuthContext';
import { Loader2, Search, CheckCircle, XCircle, RefreshCw, AlertTriangle } from 'lucide-react';

interface LookupState {
  loading: boolean;
  data: unknown;
  error: string | null;
}

const EMPTY: LookupState = { loading: false, data: null, error: null };

async function runLookup(
  fn: () => Promise<{ data: unknown }>,
  setState: React.Dispatch<React.SetStateAction<LookupState>>,
  onAuthError?: () => void,
) {
  setState({ loading: true, data: null, error: null });
  try {
    const res = await fn();
    setState({ loading: false, data: res.data, error: null });
  } catch (e: unknown) {
    const status = (e as { response?: { status?: number } }).response?.status;
    if (status === 424 && onAuthError) {
      onAuthError();
      return;
    }
    const msg = (e as { message?: string }).message ?? 'Request failed';
    setState({ loading: false, data: null, error: msg });
  }
}

// ── Amount comparison helper (mirrors DynamicErpValidator.CompareValues / CleanNumeric) ──
function cleanNumeric(value: string): string {
  // Strip currency codes, symbols, and whitespace — same regex as backend CleanNumeric
  return value.trim().replace(/[A-Za-z$£€¥\s]/g, '');
}

function compareAmounts(input: string, erpValue: string) {
  const cleanInput = cleanNumeric(input);
  const cleanErp   = cleanNumeric(erpValue);
  const parsedInput = parseFloat(cleanInput.replace(/,/g, ''));
  const parsedErp   = parseFloat(cleanErp.replace(/,/g, ''));
  const bothNumeric = !isNaN(parsedInput) && !isNaN(parsedErp);
  const match = bothNumeric
    ? parsedInput === parsedErp
    : input.trim().toLowerCase() === erpValue.trim().toLowerCase();
  return {
    match,
    cleanInput,
    cleanErp,
    parsedInput: bothNumeric ? parsedInput : null,
    parsedErp:   bothNumeric ? parsedErp   : null,
  };
}

// ── Vendor row shape ──────────────────────────────────────────────────────────
interface VendorRow { vendorId: string; vendorName: string; isActive: boolean; }

export default function AcumaticaTestPage() {
  const { logout } = useAuth();

  // Called whenever Acumatica returns 424 (token expired)
  const onAuthError = () => logout('session_expired');

  // Guard: if no Acumatica token is present, force logout before any ERP call
  const withTokenCheck = (fn: () => void) => {
    if (!sessionStorage.getItem('acumatica_token')) {
      logout('session_expired');
      return;
    }
    fn();
  };

  // Vendor preview state (top 5)
  const [vendorList, setVendorList] = useState<LookupState>(EMPTY);

  // Individual lookups
  const [vendorId,   setVendorId]     = useState('');
  const [vendorName, setVendorName]   = useState('');

  const [vendorIdResult,   setVendorIdResult]   = useState<LookupState>(EMPTY);
  const [vendorNameResult, setVendorNameResult] = useState<LookupState>(EMPTY);

  // OData entity discovery
  const [odataEntities,    setOdataEntities]    = useState<LookupState>(EMPTY);
  const [odataRaw,         setOdataRaw]         = useState<string | null>(null);
  const [odataRawLoading,  setOdataRawLoading]  = useState(false);

  // Branch lookup
  const [branchEntity,      setBranchEntity]      = useState('Branch');
  const [branchCode,        setBranchCode]        = useState('');
  const [branchProbeResult, setBranchProbeResult] = useState<LookupState>(EMPTY);
  const [branchResult,      setBranchResult]      = useState<LookupState>(EMPTY);

  // Vendor ending balance
  const [balVendorId, setBalVendorId] = useState('');
  const [balPeriod,   setBalPeriod]   = useState('');
  const [balResult,   setBalResult]   = useState<LookupState>(EMPTY);

  // AP Invoice diagnostics
  const [apProbeResult,    setApProbeResult]    = useState<LookupState>(EMPTY);
  const [apRefNbr,         setApRefNbr]         = useState('');
  const [apRefNbrResult,   setApRefNbrResult]   = useState<LookupState>(EMPTY);
  const [apVendorRef,      setApVendorRef]      = useState('');
  const [apVendorRefResult,setApVendorRefResult]= useState<LookupState>(EMPTY);
  const [amountInput,      setAmountInput]      = useState('');

  // Generic entity:field lookup (DynamicErpValidator path)
  const [genericEntity, setGenericEntity] = useState('');
  const [genericField,  setGenericField]  = useState('');
  const [genericValue,  setGenericValue]  = useState('');
  const [genericResult, setGenericResult] = useState<LookupState>(EMPTY);

  // Entity catalog (for the entity selector dropdown)
  interface EntityCatalog { entityName: string; displayName: string; filterableFields: string[] }
  const [entityCatalog, setEntityCatalog] = useState<EntityCatalog[]>([]);
  useEffect(() => {
    erpApi.getErpEntities().then(r => setEntityCatalog(r.data as EntityCatalog[])).catch(() => {});
  }, []);
  const selectedEntity = entityCatalog.find(e => e.entityName === genericEntity);

  const vendors = (vendorList.data as VendorRow[] | null) ?? [];

  return (
    <div className="space-y-6 max-w-4xl mx-auto">
      <div>
        <h1 className="text-2xl font-bold text-foreground">Acumatica Lookup Test</h1>
        <p className="text-sm text-muted-foreground mt-1">
          Test live data retrieval from Acumatica.
        </p>
      </div>

      {/* ── Vendor List (top 5 preview) ──────────────────────────────── */}
      <div className="card p-5 space-y-4">
        <div>
          <h2 className="font-semibold text-foreground">Vendor List — Top 5 Preview</h2>
          <p className="text-xs text-muted-foreground mt-0.5">
            Fetches the first 5 vendors (<code className="font-mono">$top=5</code>) to confirm the connection is working.
          </p>
        </div>

        <button
          className="btn-primary flex items-center gap-2"
          onClick={() => withTokenCheck(() => runLookup(() => erpApi.getVendors(5), setVendorList, onAuthError))}
          disabled={vendorList.loading}
        >
          {vendorList.loading
            ? <Loader2 className="h-4 w-4 animate-spin" />
            : <RefreshCw className="h-4 w-4" />}
          Fetch Top 5 Vendors
        </button>

        {vendorList.error && (
          <div className="rounded-lg border border-red-200 bg-red-50 p-3 text-sm text-red-700 flex items-center gap-2">
            <XCircle className="h-4 w-4 flex-shrink-0" /> {vendorList.error}
          </div>
        )}

        {!vendorList.loading && vendorList.data !== null && vendors.length === 0 && !vendorList.error && (
          <div className="rounded-lg border border-amber-200 bg-amber-50 p-3 text-sm text-amber-700 flex items-center gap-2">
            <XCircle className="h-4 w-4 flex-shrink-0" />
            No vendors returned — Acumatica auth may have failed. Check that you signed out and back in, then try again.
          </div>
        )}

        {vendors.length > 0 && (
          <>
            <div className="flex items-center gap-2 text-green-700">
              <CheckCircle className="h-4 w-4 flex-shrink-0" />
              <span className="text-sm font-medium">{vendors.length} vendors returned — connection OK</span>
            </div>
            <div className="overflow-x-auto rounded-lg border border-border">
              <table className="w-full text-sm">
                <thead className="bg-muted/50 border-b border-border">
                  <tr>
                    <th className="text-left px-3 py-2 font-medium text-muted-foreground">Vendor ID</th>
                    <th className="text-left px-3 py-2 font-medium text-muted-foreground">Vendor Name</th>
                    <th className="text-left px-3 py-2 font-medium text-muted-foreground">Status</th>
                  </tr>
                </thead>
                <tbody className="divide-y divide-border">
                  {vendors.map(v => (
                    <tr key={v.vendorId} className="hover:bg-muted/30">
                      <td className="px-3 py-2 font-mono text-xs">{v.vendorId}</td>
                      <td className="px-3 py-2">{v.vendorName}</td>
                      <td className="px-3 py-2">
                        <span className={`text-xs px-2 py-0.5 rounded-full font-medium ${
                          v.isActive ? 'bg-green-100 text-green-700' : 'bg-gray-100 text-gray-600'
                        }`}>
                          {v.isActive ? 'Active' : 'Inactive'}
                        </span>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </>
        )}
      </div>

      {/* ── Vendor by ID ───────────────────────────────────────────────── */}
      <LookupCard
        title="Vendor by ID"
        description='Fetches a specific vendor by Acumatica Vendor ID (used by the "VendorID" ERP mapping key).'
        inputLabel="Vendor ID"
        value={vendorId}
        onChange={setVendorId}
        placeholder="e.g. V00001"
        state={vendorIdResult}
        onLookup={() => withTokenCheck(() => runLookup(() => erpApi.lookupVendor(vendorId), setVendorIdResult, onAuthError))}
      />

      {/* ── Vendor by Name ─────────────────────────────────────────────── */}
      <LookupCard
        title="Vendor by Name"
        description='Same logic as the "VendorName" ERP mapping key — fetches ALL vendors from Acumatica then matches case-insensitively with whitespace normalization. ⚠ Results are cached 30 min; restart the API to clear the cache when debugging.'
        inputLabel="Vendor Name"
        value={vendorName}
        onChange={setVendorName}
        placeholder="e.g. ABC SDN BHD"
        state={vendorNameResult}
        onLookup={() => withTokenCheck(() => runLookup(() => erpApi.lookupVendorByName(vendorName), setVendorNameResult, onAuthError))}
      />

      {/* ── OData Entity Discovery ─────────────────────────────────────── */}
      <div className="card p-5 space-y-4">
        <div>
          <h2 className="font-semibold text-foreground">OData Entity Discovery</h2>
          <p className="text-xs text-muted-foreground mt-0.5">
            Queries <code className="font-mono text-xs">/entity/Default/{'{version}'}/</code> — the OData service document —
            to list <strong>all</strong> entity names available in your Acumatica instance.
            Use this to find the correct entity name when a lookup fails (e.g. Branch, GLBranch, etc.).
          </p>
        </div>
        <div className="flex gap-2 flex-wrap">
          <button
            className="btn-primary flex items-center gap-2"
            onClick={() => withTokenCheck(() => runLookup(() => erpApi.getODataEntities(), setOdataEntities, onAuthError))}
            disabled={odataEntities.loading}
          >
            {odataEntities.loading ? <Loader2 className="h-4 w-4 animate-spin" /> : <Search className="h-4 w-4" />}
            Discover All Entities
          </button>
          <button
            className="btn-secondary flex items-center gap-2 text-sm"
            onClick={() => withTokenCheck(async () => {
              setOdataRaw(null);
              setOdataRawLoading(true);
              try {
                const res = await erpApi.getODataEntitiesRaw();
                setOdataRaw(String(res.data));
              } catch (e: unknown) {
                setOdataRaw(`Error: ${(e as { message?: string }).message}`);
              } finally { setOdataRawLoading(false); }
            })}
            disabled={odataRawLoading}
          >
            {odataRawLoading ? <Loader2 className="h-4 w-4 animate-spin" /> : <Search className="h-4 w-4" />}
            Show Raw Response
          </button>
        </div>
        {odataRaw !== null && (
          <div className="rounded-lg border border-border bg-muted/30 p-3">
            <p className="text-xs font-semibold text-foreground mb-2">Raw Acumatica response from <code className="font-mono">/entity/Default/{'{version}'}/</code></p>
            <pre className="text-xs font-mono bg-white rounded p-3 border border-border max-h-72 overflow-auto whitespace-pre-wrap break-all">
              {odataRaw}
            </pre>
          </div>
        )}
        {(odataEntities.data !== null || odataEntities.error) && (() => {
          const names = odataEntities.data as string[] | null;
          const branchRelated = names?.filter(n => n.toLowerCase().includes('branch') || n.toLowerCase().includes('organization')) ?? [];
          return (
            <div className={`rounded-lg border p-4 space-y-3 ${odataEntities.error ? 'bg-red-50 border-red-200' : 'bg-green-50 border-green-200'}`}>
              {odataEntities.error ? (
                <div className="flex items-center gap-2 text-red-700">
                  <XCircle className="h-4 w-4 flex-shrink-0" />
                  <span className="text-sm font-medium">Failed — {odataEntities.error}</span>
                </div>
              ) : (
                <>
                  <div className="flex items-center gap-2 text-green-700">
                    <CheckCircle className="h-4 w-4 flex-shrink-0" />
                    <span className="text-sm font-medium">{names?.length ?? 0} entities available</span>
                  </div>
                  {branchRelated.length > 0 && (
                    <div className="text-xs text-blue-800 bg-blue-50 border border-blue-200 rounded p-2">
                      <span className="font-semibold">Branch-related entities found: </span>
                      {branchRelated.map(n => (
                        <button
                          key={n}
                          onClick={() => setBranchEntity(n)}
                          className="font-mono bg-white border border-blue-300 rounded px-1.5 py-0.5 mx-0.5 hover:bg-blue-100 cursor-pointer"
                        >
                          {n}
                        </button>
                      ))}
                      <span className="text-blue-600 ml-1">(click to use in Branch Lookup below)</span>
                    </div>
                  )}
                  {branchRelated.length === 0 && (
                    <p className="text-xs text-amber-700">No branch or organization entities found — branch validation may not be supported on this instance.</p>
                  )}
                  <details>
                    <summary className="text-xs text-muted-foreground cursor-pointer hover:text-foreground">All entity names ({names?.length})</summary>
                    <div className="mt-2 flex flex-wrap gap-1 max-h-48 overflow-auto">
                      {names?.map(n => (
                        <span key={n} className="font-mono text-xs bg-muted px-1.5 py-0.5 rounded border border-border">{n}</span>
                      ))}
                    </div>
                  </details>
                </>
              )}
            </div>
          );
        })()}
      </div>

      {/* ── Branch Lookup ──────────────────────────────────────────────── */}
      <div className="card p-5 space-y-5">
        <div>
          <h2 className="font-semibold text-foreground">Branch Lookup</h2>
          <p className="text-xs text-muted-foreground mt-0.5">
            Tests the <code className="font-mono text-xs">BranchID</code> ERP mapping key validation path.
            Use "Discover All Entities" above to find the correct entity name, then set it below.
          </p>
        </div>

        {/* Entity name input */}
        <div className="flex items-end gap-3">
          <div className="flex-1 max-w-xs">
            <label className="block text-xs font-medium text-foreground mb-1">Entity Name</label>
            <input
              className="input font-mono"
              value={branchEntity}
              onChange={e => { setBranchEntity(e.target.value); setBranchProbeResult(EMPTY); setBranchResult(EMPTY); }}
              placeholder="e.g. Branch, GLBranch, Organization"
            />
          </div>
          <p className="text-xs text-muted-foreground pb-2">
            Used for both the probe and lookup below.
          </p>
        </div>

        {/* Step 1 — probe */}
        <div className="space-y-2">
          <p className="text-xs font-semibold text-foreground uppercase tracking-wide">
            Step 1 — Probe endpoint (no filter, first record)
          </p>
          <p className="text-xs text-muted-foreground">
            Calls <code className="font-mono">{branchEntity}?$top=1</code> — confirms auth works and shows actual OData field names.
          </p>
          <button
            className="btn-primary flex items-center gap-2"
            onClick={() => withTokenCheck(() => runLookup(() => erpApi.probeEntity(branchEntity), setBranchProbeResult, onAuthError))}
            disabled={branchProbeResult.loading || !branchEntity.trim()}
          >
            {branchProbeResult.loading ? <Loader2 className="h-4 w-4 animate-spin" /> : <Search className="h-4 w-4" />}
            Probe Branch
          </button>
          {(branchProbeResult.data !== null || branchProbeResult.error) && (() => {
            const result = branchProbeResult.data as { found?: boolean; data?: unknown; errorMessage?: string } | null;
            return (
              <div className={`rounded-lg border p-4 space-y-2 ${
                branchProbeResult.error ? 'bg-red-50 border-red-200'
                : result?.found         ? 'bg-green-50 border-green-200'
                : 'bg-amber-50 border-amber-200'
              }`}>
                {branchProbeResult.error ? (
                  <div className="flex items-center gap-2 text-red-700">
                    <XCircle className="h-4 w-4 flex-shrink-0" />
                    <span className="text-sm font-medium">Request failed — check Docker logs</span>
                  </div>
                ) : result?.found ? (
                  <div className="flex items-center gap-2 text-green-700">
                    <CheckCircle className="h-4 w-4 flex-shrink-0" />
                    <span className="text-sm font-medium">Endpoint OK — field names shown below</span>
                  </div>
                ) : (
                  <div className="flex items-center gap-2 text-amber-700">
                    <AlertTriangle className="h-4 w-4 flex-shrink-0" />
                    <span className="text-sm font-medium">{result?.errorMessage ?? 'Not found'}</span>
                  </div>
                )}
                <pre className="text-xs font-mono bg-white/60 rounded p-3 border border-border/50 max-h-72 overflow-auto whitespace-pre-wrap break-all">
                  {JSON.stringify(branchProbeResult.error ?? result, null, 2)}
                </pre>
              </div>
            );
          })()}
        </div>

        <hr className="border-border" />

        {/* Step 2 — lookup by BranchID */}
        <div className="space-y-2">
          <p className="text-xs font-semibold text-foreground uppercase tracking-wide">
            Step 2 — Lookup by Branch Code
          </p>
          <p className="text-xs text-muted-foreground">
            Filters by <code className="font-mono">BranchID eq '…'</code> on the entity selected above.
            If BranchID doesn't exist, try the field name shown in the probe result (Step 1).
          </p>
          <div className="flex flex-wrap gap-3 items-end">
            <div className="flex-1 min-w-[160px]">
              <label className="block text-xs font-medium text-foreground mb-1">Branch Code / Value</label>
              <input
                className="input"
                value={branchCode}
                onChange={e => setBranchCode(e.target.value)}
                placeholder="e.g. HQ"
                onKeyDown={e => {
                  if (e.key === 'Enter' && branchCode.trim())
                    withTokenCheck(() => runLookup(() => erpApi.lookupGeneric(branchEntity, 'BranchID', branchCode), setBranchResult, onAuthError));
                }}
              />
            </div>
            <button
              className="btn-primary flex items-center gap-2 flex-shrink-0"
              onClick={() => withTokenCheck(() => runLookup(() => erpApi.lookupGeneric(branchEntity, 'BranchID', branchCode), setBranchResult, onAuthError))}
              disabled={!branchCode.trim() || !branchEntity.trim() || branchResult.loading}
            >
              {branchResult.loading ? <Loader2 className="h-4 w-4 animate-spin" /> : <Search className="h-4 w-4" />}
              Lookup
            </button>
          </div>
          <InlineResult state={branchResult} />
        </div>
      </div>

      {/* ── Generic Entity:Field Lookup ─────────────────────────────────── */}
      <div className="card p-5 space-y-4">
        <div>
          <h2 className="font-semibold text-foreground">Generic Entity : Field Lookup</h2>
          <p className="text-xs text-muted-foreground mt-0.5">
            Same code path as <code className="font-mono text-xs">Entity:Field</code> ERP mapping keys (e.g.{' '}
            <code className="font-mono text-xs">Vendor:VendorName</code>,{' '}
            <code className="font-mono text-xs">Bill:VendorRef</code>).
            Hits Acumatica directly — no cache.
          </p>
        </div>

        <div className="flex flex-wrap gap-3 items-end">
          {/* Entity selector */}
          <div className="w-44">
            <label className="block text-xs font-medium text-foreground mb-1">Entity</label>
            <select
              className="input text-sm"
              value={genericEntity}
              onChange={e => { setGenericEntity(e.target.value); setGenericField(''); }}
            >
              <option value="">— select —</option>
              {entityCatalog.map(e => (
                <option key={e.entityName} value={e.entityName}>{e.displayName}</option>
              ))}
            </select>
          </div>

          {/* Field selector (populated from entity catalog) */}
          <div className="w-48">
            <label className="block text-xs font-medium text-foreground mb-1">Field</label>
            {selectedEntity ? (
              <select
                className="input text-sm"
                value={genericField}
                onChange={e => setGenericField(e.target.value)}
              >
                <option value="">— select —</option>
                {selectedEntity.filterableFields.map(f => (
                  <option key={f} value={f}>{f}</option>
                ))}
              </select>
            ) : (
              <input className="input text-sm text-muted-foreground" disabled placeholder="Select entity first" />
            )}
          </div>

          {/* Value input */}
          <div className="flex-1 min-w-[160px]">
            <label className="block text-xs font-medium text-foreground mb-1">Value to match</label>
            <input
              className="input"
              value={genericValue}
              onChange={e => setGenericValue(e.target.value)}
              placeholder="e.g. ABC SDN BHD"
              onKeyDown={e => {
                if (e.key === 'Enter' && genericEntity && genericField && genericValue.trim())
                  withTokenCheck(() => runLookup(() => erpApi.lookupGeneric(genericEntity, genericField, genericValue), setGenericResult, onAuthError));
              }}
            />
          </div>

          <button
            className="btn-primary flex items-center gap-2 flex-shrink-0"
            disabled={!genericEntity || !genericField || !genericValue.trim() || genericResult.loading}
            onClick={() => withTokenCheck(() => runLookup(() => erpApi.lookupGeneric(genericEntity, genericField, genericValue), setGenericResult, onAuthError))}
          >
            {genericResult.loading ? <Loader2 className="h-4 w-4 animate-spin" /> : <Search className="h-4 w-4" />}
            Lookup
          </button>
        </div>

        {(genericEntity && genericField) && (
          <p className="text-xs text-muted-foreground font-mono bg-muted/50 rounded px-2 py-1 inline-block">
            ERP key equivalent: <span className="text-foreground font-semibold">{genericEntity}:{genericField}</span>
          </p>
        )}

        {(genericResult.data !== null || genericResult.error) && (() => {
          const result = genericResult.data as { found?: boolean; data?: unknown; errorMessage?: string } | null;
          return (
            <div className={`rounded-lg border p-4 space-y-2 ${
              genericResult.error ? 'bg-red-50 border-red-200'
              : result?.found     ? 'bg-green-50 border-green-200'
              : 'bg-amber-50 border-amber-200'
            }`}>
              {genericResult.error ? (
                <div className="flex items-center gap-2 text-red-700">
                  <XCircle className="h-4 w-4 flex-shrink-0" />
                  <span className="text-sm font-medium">Request failed</span>
                </div>
              ) : result?.found ? (
                <div className="flex items-center gap-2 text-green-700">
                  <CheckCircle className="h-4 w-4 flex-shrink-0" />
                  <span className="text-sm font-medium">Found — <code className="font-mono">{genericEntity}.{genericField} = "{genericValue}"</code></span>
                </div>
              ) : (
                <div className="flex items-center gap-2 text-amber-700">
                  <AlertTriangle className="h-4 w-4 flex-shrink-0" />
                  <span className="text-sm font-medium">Not found — {result?.errorMessage ?? 'No match'}</span>
                </div>
              )}
              <pre className="text-xs font-mono bg-white/60 rounded p-3 border border-border/50 max-h-72 overflow-auto whitespace-pre-wrap break-all">
                {JSON.stringify(genericResult.error ?? result, null, 2)}
              </pre>
            </div>
          );
        })()}
      </div>

      {/* ── AP Invoice Diagnostics ─────────────────────────────────────── */}
      <div className="card p-5 space-y-5">
        <div>
          <h2 className="font-semibold text-foreground">AP Invoice Diagnostics</h2>
          <p className="text-xs text-muted-foreground mt-0.5">
            Step 1 — probe the endpoint to verify auth and discover field names.
            Step 2 — look up by RefNbr or VendorRef.
          </p>
        </div>

        {/* Step 1 — probe */}
        <div className="space-y-2">
          <p className="text-xs font-semibold text-foreground uppercase tracking-wide">
            Step 1 — Probe endpoint (no filter, first record)
          </p>
          <p className="text-xs text-muted-foreground">
            Calls <code className="font-mono">APInvoice?$top=1</code> to confirm auth works
            and show actual OData field names from your Acumatica version.
          </p>
          <button
            className="btn-primary flex items-center gap-2"
            onClick={() => withTokenCheck(() => runLookup(() => erpApi.probeEntity('Bill'), setApProbeResult, onAuthError))}
            disabled={apProbeResult.loading}
          >
            {apProbeResult.loading ? <Loader2 className="h-4 w-4 animate-spin" /> : <Search className="h-4 w-4" />}
            Probe Bill (AP Invoice)
          </button>
          {(apProbeResult.data !== null || apProbeResult.error) && (() => {
            const result = apProbeResult.data as { found?: boolean; data?: unknown; errorMessage?: string } | null;
            return (
              <div className={`rounded-lg border p-4 space-y-2 ${
                apProbeResult.error ? 'bg-red-50 border-red-200'
                : result?.found     ? 'bg-green-50 border-green-200'
                : 'bg-amber-50 border-amber-200'
              }`}>
                {apProbeResult.error ? (
                  <div className="flex items-center gap-2 text-red-700">
                    <XCircle className="h-4 w-4 flex-shrink-0" />
                    <span className="text-sm font-medium">Request failed — check Docker logs</span>
                  </div>
                ) : result?.found ? (
                  <div className="flex items-center gap-2 text-green-700">
                    <CheckCircle className="h-4 w-4 flex-shrink-0" />
                    <span className="text-sm font-medium">Endpoint OK — field names shown below</span>
                  </div>
                ) : (
                  <div className="flex items-center gap-2 text-amber-700">
                    <AlertTriangle className="h-4 w-4 flex-shrink-0" />
                    <span className="text-sm font-medium">{result?.errorMessage ?? 'Not found'}</span>
                  </div>
                )}
                <pre className="text-xs font-mono bg-white/60 rounded p-3 border border-border/50 max-h-72 overflow-auto whitespace-pre-wrap break-all">
                  {JSON.stringify(apProbeResult.error ?? result, null, 2)}
                </pre>
              </div>
            );
          })()}
        </div>

        <hr className="border-border" />

        {/* Step 2a — RefNbr */}
        <div className="space-y-2">
          <p className="text-xs font-semibold text-foreground uppercase tracking-wide">
            Step 2a — Lookup by RefNbr (dedicated path)
          </p>
          <p className="text-xs text-muted-foreground">
            Uses <code className="font-mono">APInvoice?$filter=RefNbr eq '…'</code> — dedicated endpoint with <code className="font-mono">DocType</code> in response.
          </p>
          <div className="flex gap-3 items-end">
            <div className="flex-1">
              <label className="block text-xs font-medium text-foreground mb-1">Reference Number (RefNbr)</label>
              <input className="input" value={apRefNbr} onChange={e => setApRefNbr(e.target.value)}
                placeholder="e.g. 000123" onKeyDown={e => { if (e.key === 'Enter' && apRefNbr.trim()) withTokenCheck(() => runLookup(() => erpApi.lookupApInvoice(apRefNbr), setApRefNbrResult, onAuthError)); }} />
            </div>
            <button className="btn-primary flex items-center gap-2 flex-shrink-0"
              onClick={() => withTokenCheck(() => runLookup(() => erpApi.lookupApInvoice(apRefNbr), setApRefNbrResult, onAuthError))}
              disabled={!apRefNbr.trim() || apRefNbrResult.loading}>
              {apRefNbrResult.loading ? <Loader2 className="h-4 w-4 animate-spin" /> : <Search className="h-4 w-4" />}
              Lookup
            </button>
          </div>
          <InlineResult state={apRefNbrResult} />
        </div>

        <hr className="border-border" />

        {/* Step 2b — VendorRef */}
        <div className="space-y-2">
          <p className="text-xs font-semibold text-foreground uppercase tracking-wide">
            Step 2b — Lookup by VendorRef (generic OData path)
          </p>
          <p className="text-xs text-muted-foreground">
            Same code path as <code className="font-mono">APInvoice:VendorRef</code> ERP mapping key.
            Uses <code className="font-mono">$filter=tolower(VendorRef) eq '…'</code> with exact fallback.
          </p>
          <div className="flex gap-3 items-end">
            <div className="flex-1">
              <label className="block text-xs font-medium text-foreground mb-1">Vendor Reference (VendorRef)</label>
              <input className="input" value={apVendorRef} onChange={e => { setApVendorRef(e.target.value); setAmountInput(''); }}
                placeholder="e.g. INV-2024-001" onKeyDown={e => { if (e.key === 'Enter' && apVendorRef.trim()) withTokenCheck(() => runLookup(() => erpApi.lookupGeneric('Bill', 'VendorRef', apVendorRef), setApVendorRefResult, onAuthError)); }} />
            </div>
            <button className="btn-primary flex items-center gap-2 flex-shrink-0"
              onClick={() => withTokenCheck(() => { setAmountInput(''); runLookup(() => erpApi.lookupGeneric('Bill', 'VendorRef', apVendorRef), setApVendorRefResult, onAuthError); })}
              disabled={!apVendorRef.trim() || apVendorRefResult.loading}>
              {apVendorRefResult.loading ? <Loader2 className="h-4 w-4 animate-spin" /> : <Search className="h-4 w-4" />}
              Lookup
            </button>
          </div>
          <InlineResult state={apVendorRefResult} />
        </div>

        {/* Step 3 — Amount comparison (shown only after a successful VendorRef lookup) */}
        {(() => {
          const vendorRefData = apVendorRefResult.data as { found?: boolean; data?: Record<string, string> } | null;
          if (!vendorRefData?.found || !vendorRefData.data) return null;
          const erpAmount = vendorRefData.data['Amount'] ?? vendorRefData.data['amount'] ?? null;
          const comparison = amountInput.trim() ? compareAmounts(amountInput.trim(), erpAmount ?? '') : null;
          return (
            <div className="space-y-3 rounded-lg border border-blue-200 bg-blue-50 p-4">
              <div>
                <p className="text-xs font-semibold text-blue-800 uppercase tracking-wide">
                  Step 3 — Compare Amount
                </p>
                <p className="text-xs text-blue-700 mt-0.5">
                  Bill found. Acumatica <strong>Amount</strong> field ={' '}
                  {erpAmount !== null
                    ? <code className="font-mono bg-white/60 rounded px-1">{erpAmount}</code>
                    : <span className="italic text-amber-700">not returned in record</span>}
                </p>
              </div>
              <div className="flex gap-3 items-end">
                <div className="flex-1">
                  <label className="block text-xs font-medium text-foreground mb-1">
                    Enter amount to compare (e.g. <code className="font-mono">MYR 20,624.65</code> or <code className="font-mono">20624.65</code>)
                  </label>
                  <input
                    className="input"
                    value={amountInput}
                    onChange={e => setAmountInput(e.target.value)}
                    placeholder="e.g. MYR 20,624.65"
                  />
                </div>
              </div>
              {comparison !== null && erpAmount !== null && (
                <div className={`rounded-lg border p-3 space-y-1 ${
                  comparison.match ? 'bg-green-50 border-green-200' : 'bg-amber-50 border-amber-200'
                }`}>
                  <div className={`flex items-center gap-2 ${comparison.match ? 'text-green-700' : 'text-amber-700'}`}>
                    {comparison.match
                      ? <CheckCircle className="h-4 w-4 flex-shrink-0" />
                      : <AlertTriangle className="h-4 w-4 flex-shrink-0" />}
                    <span className="text-sm font-semibold">
                      {comparison.match ? 'Amount matches!' : 'Amount mismatch'}
                    </span>
                  </div>
                  <div className="text-xs font-mono space-y-0.5 mt-1">
                    <div>You entered: <span className="font-semibold">{amountInput.trim()}</span> → cleaned: <span className="font-semibold">{comparison.cleanInput}</span> → parsed: <span className="font-semibold">{comparison.parsedInput ?? '(not a number)'}</span></div>
                    <div>Acumatica:   <span className="font-semibold">{erpAmount}</span> → cleaned: <span className="font-semibold">{comparison.cleanErp}</span> → parsed: <span className="font-semibold">{comparison.parsedErp ?? '(not a number)'}</span></div>
                    <div className="mt-1">
                      {comparison.parsedInput !== null && comparison.parsedErp !== null
                        ? `Numeric comparison: ${comparison.parsedInput} ${comparison.match ? '==' : '!='} ${comparison.parsedErp}`
                        : 'Falling back to case-insensitive string comparison'}
                    </div>
                  </div>
                </div>
              )}
              {comparison !== null && erpAmount === null && (
                <div className="rounded-lg border border-amber-200 bg-amber-50 p-3 flex items-center gap-2 text-amber-700 text-sm">
                  <AlertTriangle className="h-4 w-4 flex-shrink-0" />
                  The bill record does not contain an <code className="font-mono">Amount</code> field — check the field name in the probe result above.
                </div>
              )}
            </div>
          );
        })()}
      </div>

      {/* ── Vendor Ending Balance ──────────────────────────────────────── */}
      <div className="card p-5 space-y-4">
        <div>
          <h2 className="font-semibold text-foreground">Vendor Ending Balance</h2>
          <p className="text-xs text-muted-foreground mt-0.5">
            Queries Acumatica <code className="font-mono text-xs">APHistory</code> for the AP ending
            balance of a vendor in a specific financial period.
            Period format: <code className="font-mono text-xs">YYYYMM</code> (e.g.{' '}
            <code className="font-mono text-xs">202501</code> for January 2025).
          </p>
        </div>

        <div className="flex flex-wrap gap-3 items-end">
          <div className="flex-1 min-w-[160px]">
            <label className="block text-xs font-medium text-foreground mb-1">Vendor ID</label>
            <input
              className="input"
              value={balVendorId}
              onChange={e => setBalVendorId(e.target.value)}
              placeholder="e.g. V00001"
              onKeyDown={e => {
                if (e.key === 'Enter' && balVendorId.trim() && balPeriod.trim())
                  withTokenCheck(() => runLookup(() => erpApi.lookupVendorBalance(balVendorId, balPeriod), setBalResult, onAuthError));
              }}
            />
          </div>
          <div className="w-36">
            <label className="block text-xs font-medium text-foreground mb-1">Period (YYYYMM)</label>
            <input
              className="input"
              value={balPeriod}
              onChange={e => setBalPeriod(e.target.value)}
              placeholder="e.g. 202501"
              onKeyDown={e => {
                if (e.key === 'Enter' && balVendorId.trim() && balPeriod.trim())
                  withTokenCheck(() => runLookup(() => erpApi.lookupVendorBalance(balVendorId, balPeriod), setBalResult, onAuthError));
              }}
            />
          </div>
          <button
            className="btn-primary flex items-center gap-2 flex-shrink-0"
            disabled={!balVendorId.trim() || !balPeriod.trim() || balResult.loading}
            onClick={() => withTokenCheck(() => runLookup(() => erpApi.lookupVendorBalance(balVendorId, balPeriod), setBalResult, onAuthError))}
          >
            {balResult.loading ? <Loader2 className="h-4 w-4 animate-spin" /> : <Search className="h-4 w-4" />}
            Lookup
          </button>
        </div>

        {(balResult.data !== null || balResult.error) && (() => {
          const result = balResult.data as { found?: boolean; data?: Record<string, string>; errorMessage?: string } | null;
          const endBal = result?.data?.['EndBalance'] ?? result?.data?.['endBalance'] ?? result?.data?.['EndBal'] ?? null;
          return (
            <div className={`rounded-lg border p-4 space-y-2 ${
              balResult.error ? 'bg-red-50 border-red-200'
              : result?.found ? 'bg-green-50 border-green-200'
              : 'bg-amber-50 border-amber-200'
            }`}>
              {balResult.error ? (
                <div className="flex items-center gap-2 text-red-700">
                  <XCircle className="h-4 w-4 flex-shrink-0" />
                  <span className="text-sm font-medium">Request failed</span>
                </div>
              ) : result?.found ? (
                <div className="space-y-1">
                  <div className="flex items-center gap-2 text-green-700">
                    <CheckCircle className="h-4 w-4 flex-shrink-0" />
                    <span className="text-sm font-medium">
                      Record found — Vendor: <code className="font-mono">{balVendorId}</code>, Period: <code className="font-mono">{balPeriod}</code>
                      {endBal !== null && <> — Ending Balance: <span className="font-bold">{endBal}</span></>}
                    </span>
                  </div>
                </div>
              ) : (
                <div className="flex items-center gap-2 text-amber-700">
                  <AlertTriangle className="h-4 w-4 flex-shrink-0" />
                  <span className="text-sm font-medium">Not found — {result?.errorMessage ?? 'No match'}</span>
                </div>
              )}
              <pre className="text-xs font-mono bg-white/60 rounded p-3 border border-border/50 max-h-72 overflow-auto whitespace-pre-wrap break-all">
                {JSON.stringify(balResult.error ?? result, null, 2)}
              </pre>
            </div>
          );
        })()}
      </div>
    </div>
  );
}

// ── Inline result panel (compact, used inside multi-step cards) ───────────────
function InlineResult({ state }: { state: LookupState }) {
  if (state.data === null && !state.error) return null;
  const result = state.data as { found?: boolean; data?: unknown; errorMessage?: string } | null;
  return (
    <div className={`rounded-lg border p-3 space-y-2 ${
      state.error     ? 'bg-red-50 border-red-200'
      : result?.found ? 'bg-green-50 border-green-200'
      : 'bg-amber-50 border-amber-200'
    }`}>
      {state.error ? (
        <div className="flex items-center gap-2 text-red-700">
          <XCircle className="h-4 w-4 flex-shrink-0" />
          <span className="text-sm font-medium">Request failed</span>
        </div>
      ) : result?.found ? (
        <div className="flex items-center gap-2 text-green-700">
          <CheckCircle className="h-4 w-4 flex-shrink-0" />
          <span className="text-sm font-medium">Found</span>
        </div>
      ) : (
        <div className="flex items-center gap-2 text-amber-700">
          <AlertTriangle className="h-4 w-4 flex-shrink-0" />
          <span className="text-sm font-medium">Not found — {result?.errorMessage ?? 'No match'}</span>
        </div>
      )}
      <pre className="text-xs font-mono bg-white/60 rounded p-3 border border-border/50 max-h-64 overflow-auto whitespace-pre-wrap break-all">
        {JSON.stringify(state.error ?? result, null, 2)}
      </pre>
    </div>
  );
}

// ── Reusable single-lookup card ───────────────────────────────────────────────

function LookupCard({
  title, description, inputLabel, value, onChange, placeholder, state, onLookup,
}: {
  title: string; description: string; inputLabel: string;
  value: string; onChange: (v: string) => void; placeholder: string;
  state: LookupState; onLookup: () => void;
}) {
  const result = state.data as { found?: boolean; data?: unknown; errorMessage?: string } | null;

  return (
    <div className="card p-5 space-y-4">
      <div>
        <h2 className="font-semibold text-foreground">{title}</h2>
        <p className="text-xs text-muted-foreground mt-0.5">{description}</p>
      </div>

      <div className="flex gap-3 items-end">
        <div className="flex-1">
          <label className="block text-xs font-medium text-foreground mb-1">{inputLabel}</label>
          <input
            className="input"
            value={value}
            onChange={e => onChange(e.target.value)}
            placeholder={placeholder}
            onKeyDown={e => { if (e.key === 'Enter' && value.trim()) onLookup(); }}
          />
        </div>
        <button
          className="btn-primary flex items-center gap-2 flex-shrink-0"
          onClick={onLookup}
          disabled={!value.trim() || state.loading}
        >
          {state.loading ? <Loader2 className="h-4 w-4 animate-spin" /> : <Search className="h-4 w-4" />}
          Lookup
        </button>
      </div>

      {(result !== null || state.error) && (
        <div className={`rounded-lg border p-4 space-y-2 ${
          state.error     ? 'bg-red-50 border-red-200'
          : result?.found ? 'bg-green-50 border-green-200'
          : 'bg-amber-50 border-amber-200'
        }`}>
          {state.error ? (
            <div className="flex items-center gap-2 text-red-700">
              <XCircle className="h-4 w-4 flex-shrink-0" />
              <span className="text-sm font-medium">Request failed</span>
            </div>
          ) : result?.found ? (
            <div className="flex items-center gap-2 text-green-700">
              <CheckCircle className="h-4 w-4 flex-shrink-0" />
              <span className="text-sm font-medium">Found</span>
            </div>
          ) : (
            <div className="flex items-center gap-2 text-amber-700">
              <XCircle className="h-4 w-4 flex-shrink-0" />
              <span className="text-sm font-medium">Not found — {result?.errorMessage ?? 'No match in Acumatica'}</span>
            </div>
          )}
          <pre className="text-xs font-mono bg-white/60 rounded p-3 border border-border/50 max-h-72 overflow-auto whitespace-pre-wrap break-all">
            {JSON.stringify(state.error ?? result, null, 2)}
          </pre>
        </div>
      )}
    </div>
  );
}
