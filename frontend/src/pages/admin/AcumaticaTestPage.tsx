import { useState, useEffect } from 'react';
import { erpApi } from '../../api/client';
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
) {
  setState({ loading: true, data: null, error: null });
  try {
    const res = await fn();
    setState({ loading: false, data: res.data, error: null });
  } catch (e: unknown) {
    const msg = (e as { message?: string }).message ?? 'Request failed';
    setState({ loading: false, data: null, error: msg });
  }
}

// ── Vendor row shape ──────────────────────────────────────────────────────────
interface VendorRow { vendorId: string; vendorName: string; isActive: boolean; }

export default function AcumaticaTestPage() {
  // Vendor preview state (top 5)
  const [vendorList, setVendorList] = useState<LookupState>(EMPTY);

  // Individual lookups
  const [vendorId,   setVendorId]     = useState('');
  const [vendorName, setVendorName]   = useState('');
  const [currency,   setCurrency]     = useState('');
  const [branch,     setBranch]       = useState('');

  const [vendorIdResult,   setVendorIdResult]   = useState<LookupState>(EMPTY);
  const [vendorNameResult, setVendorNameResult] = useState<LookupState>(EMPTY);
  const [currencyResult,   setCurrencyResult]   = useState<LookupState>(EMPTY);
  const [branchResult,     setBranchResult]     = useState<LookupState>(EMPTY);

  // AP Invoice diagnostics
  const [apProbeResult,    setApProbeResult]    = useState<LookupState>(EMPTY);
  const [apRefNbr,         setApRefNbr]         = useState('');
  const [apRefNbrResult,   setApRefNbrResult]   = useState<LookupState>(EMPTY);
  const [apVendorRef,      setApVendorRef]      = useState('');
  const [apVendorRefResult,setApVendorRefResult]= useState<LookupState>(EMPTY);

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
          onClick={() => runLookup(() => erpApi.getVendors(5), setVendorList)}
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
        onLookup={() => runLookup(() => erpApi.lookupVendor(vendorId), setVendorIdResult)}
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
        onLookup={() => runLookup(() => erpApi.lookupVendorByName(vendorName), setVendorNameResult)}
      />

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
                  runLookup(() => erpApi.lookupGeneric(genericEntity, genericField, genericValue), setGenericResult);
              }}
            />
          </div>

          <button
            className="btn-primary flex items-center gap-2 flex-shrink-0"
            disabled={!genericEntity || !genericField || !genericValue.trim() || genericResult.loading}
            onClick={() => runLookup(() => erpApi.lookupGeneric(genericEntity, genericField, genericValue), setGenericResult)}
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
            onClick={() => runLookup(() => erpApi.probeEntity('Bill'), setApProbeResult)}
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
                placeholder="e.g. 000123" onKeyDown={e => { if (e.key === 'Enter' && apRefNbr.trim()) runLookup(() => erpApi.lookupApInvoice(apRefNbr), setApRefNbrResult); }} />
            </div>
            <button className="btn-primary flex items-center gap-2 flex-shrink-0"
              onClick={() => runLookup(() => erpApi.lookupApInvoice(apRefNbr), setApRefNbrResult)}
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
              <input className="input" value={apVendorRef} onChange={e => setApVendorRef(e.target.value)}
                placeholder="e.g. INV-2024-001" onKeyDown={e => { if (e.key === 'Enter' && apVendorRef.trim()) runLookup(() => erpApi.lookupGeneric('Bill', 'VendorRef', apVendorRef), setApVendorRefResult); }} />
            </div>
            <button className="btn-primary flex items-center gap-2 flex-shrink-0"
              onClick={() => runLookup(() => erpApi.lookupGeneric('Bill', 'VendorRef', apVendorRef), setApVendorRefResult)}
              disabled={!apVendorRef.trim() || apVendorRefResult.loading}>
              {apVendorRefResult.loading ? <Loader2 className="h-4 w-4 animate-spin" /> : <Search className="h-4 w-4" />}
              Lookup
            </button>
          </div>
          <InlineResult state={apVendorRefResult} />
        </div>
      </div>

      {/* ── Currency ───────────────────────────────────────────────────── */}
      <LookupCard
        title="Currency"
        description='Validates a currency code (used by the "CurrencyID" ERP mapping key).'
        inputLabel="Currency Code"
        value={currency}
        onChange={setCurrency}
        placeholder="e.g. MYR"
        state={currencyResult}
        onLookup={() => runLookup(() => erpApi.lookupCurrency(currency), setCurrencyResult)}
      />

      {/* ── Branch ─────────────────────────────────────────────────────── */}
      <LookupCard
        title="Branch"
        description='Looks up an Acumatica branch by code (used by the "BranchID" ERP mapping key).'
        inputLabel="Branch Code"
        value={branch}
        onChange={setBranch}
        placeholder="e.g. MAIN"
        state={branchResult}
        onLookup={() => runLookup(() => erpApi.lookupBranch(branch), setBranchResult)}
      />
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
