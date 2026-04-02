using ClosedXML.Excel;
using Hangfire;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OcrSystem.Application.CashFlow;
using OcrSystem.Application.ERP;
using OcrSystem.Domain.Enums;
using OcrSystem.Infrastructure.Persistence;
using System.Text.RegularExpressions;

namespace OcrSystem.API.Controllers;

[ApiController]
[Route("api/cash-flow")]
[Authorize(Policy = "ManagerAndAbove")]
public class CashFlowController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IErpIntegrationService _erp;
    private readonly IAgingSnapshotService _snapshot;
    private readonly IBackgroundJobClient _jobs;
    private readonly ICaptureProgressTracker _captureProgress;

    private const string XlsxMime = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    public CashFlowController(AppDbContext db, IErpIntegrationService erp, IAgingSnapshotService snapshot, IBackgroundJobClient jobs, ICaptureProgressTracker captureProgress)
    {
        _db = db;
        _erp = erp;
        _snapshot = snapshot;
        _jobs = jobs;
        _captureProgress = captureProgress;
    }

    [HttpGet("aging")]
    public async Task<IActionResult> GetAgingReport(CancellationToken ct)
    {
        var refDate = DateTimeOffset.UtcNow;

        // ── 1. Vendor-statement document types ───────────────────────────
        var vsTypeIds = await _db.DocumentTypes
            .Where(dt => dt.Category == DocumentCategory.VendorStatement)
            .Select(dt => dt.Id)
            .ToListAsync(ct);

        if (vsTypeIds.Count == 0)
            return Ok(EmptyReport(refDate));

        // ── 2. Latest statement doc per vendor ────────────────────────────
        var processedStatuses = new[]
        {
            DocumentStatus.PendingReview,
            DocumentStatus.ReviewInProgress,
            DocumentStatus.Approved,
            DocumentStatus.Checked,
            DocumentStatus.Pushed,
        };

        var allStatementDocs = await _db.Documents
            .Where(d => d.DocumentTypeId != null &&
                        vsTypeIds.Contains(d.DocumentTypeId.Value) &&
                        processedStatuses.Contains(d.Status))
            .Include(d => d.Vendor)
            .Include(d => d.Branch)
            .Include(d => d.UploadedByUser)
            .OrderByDescending(d => d.UploadedAt)
            .ToListAsync(ct);

        var latestByVendor = allStatementDocs
            .GroupBy(d => d.VendorId.HasValue
                ? d.VendorId.Value.ToString()
                : (d.VendorName ?? $"__noid_{d.Id}"))
            .Select(g => g.First())
            .ToList();

        if (latestByVendor.Count == 0)
            return Ok(EmptyReport(refDate));

        // ── 3. Manager display names ──────────────────────────────────────
        var managerIds = latestByVendor
            .Select(d => d.ApprovedBy ?? d.ReviewedBy ?? d.UploadedBy)
            .Distinct().ToList();

        var managerNames = await _db.Users
            .Where(u => managerIds.Contains(u.Id))
            .Select(u => new { u.Id, u.DisplayName, u.Username })
            .ToDictionaryAsync(u => u.Id, u => u.DisplayName ?? u.Username, ct);

        // ── 4. OCR extracted fields per doc ──────────────────────────────
        var docIds = latestByVendor.Select(d => d.Id).ToList();

        var ocrResults = await _db.OcrResults
            .Where(r => docIds.Contains(r.DocumentId))
            .Include(r => r.ExtractedFields)
            .ToListAsync(ct);

        var fieldsByDocId = ocrResults
            .GroupBy(r => r.DocumentId)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(r => r.VersionNumber).First().ExtractedFields.ToList());

        // ── 4b. Pre-extract statement dates (needed for GI AgeDate) ──────
        static DateTimeOffset? ParseStmtDate(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            if (DateTimeOffset.TryParse(raw, out var dto)) return dto;
            if (DateTime.TryParseExact(raw,
                    ["dd.MM.yyyy", "dd/MM/yyyy", "d/M/yyyy", "d.M.yyyy"],
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var dt))
                return new DateTimeOffset(dt, TimeSpan.Zero);
            return null;
        }

        var stmtDateByDocId = new Dictionary<Guid, DateTimeOffset?>();
        foreach (var doc in latestByVendor)
        {
            if (!fieldsByDocId.TryGetValue(doc.Id, out var flds)) continue;
            var fld = flds.FirstOrDefault(x =>
                string.Equals(x.FieldName, "statementDate", StringComparison.OrdinalIgnoreCase));
            if (fld is null) continue;
            var raw = fld.CorrectedValue ?? fld.NormalizedValue ?? fld.RawValue;
            stmtDateByDocId[doc.Id] = ParseStmtDate(raw ?? "");
        }

        // ── 5. Load Kind 0 snapshots (AP aging at statement date) ────────
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var latestKind0Date = await _db.VendorAgingSnapshots
            .Where(s => s.SnapshotDate <= today && s.SnapshotKind == 0)
            .Select(s => (DateOnly?)s.SnapshotDate)
            .MaxAsync(ct);

        Dictionary<string, Domain.Entities.VendorAgingSnapshot> snapshotByKey = [];
        if (latestKind0Date.HasValue)
        {
            var snapList = await _db.VendorAgingSnapshots
                .Where(s => s.SnapshotDate == latestKind0Date.Value && s.SnapshotKind == 0)
                .ToListAsync(ct);
            snapshotByKey = snapList.ToDictionary(s => s.VendorLocalId, s => s);
        }

        // ── 6. Build per-vendor result ────────────────────────────────────
        static decimal? GetFieldStatic(IList<Domain.Entities.ExtractedField> fields, params string[] names)
        {
            foreach (var name in names)
            {
                var fld = fields.FirstOrDefault(x =>
                    string.Equals(x.FieldName, name, StringComparison.OrdinalIgnoreCase));
                if (fld is null) continue;
                var val = fld.CorrectedValue ?? fld.NormalizedValue ?? fld.RawValue;
                if (string.IsNullOrWhiteSpace(val)) continue;
                var cleaned = Regex.Replace(val, @"[^\d\.\-]", "");
                if (decimal.TryParse(cleaned, System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out var result))
                    return result;
            }
            return null;
        }

        static decimal ParseAmt(string raw)
        {
            var cleaned = Regex.Replace(raw ?? "", @"[^\d\.\-]", "");
            return decimal.TryParse(cleaned, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0m;
        }

        static DateTime? ParseInvoiceDate(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            if (DateTime.TryParseExact(raw,
                    ["dd.MM.yyyy", "dd/MM/yyyy", "d/M/yyyy", "d.M.yyyy",
                     "dd-MM-yyyy", "yyyy-MM-dd"],
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var dt))
                return dt;
            return DateTime.TryParse(raw, out var dt2) ? dt2 : null;
        }

        var vendors = latestByVendor.Select(doc =>
        {
            var groupKey = doc.VendorId.HasValue
                ? doc.VendorId.Value.ToString()
                : (doc.VendorName ?? $"__noid_{doc.Id}");

            var fields   = fieldsByDocId.TryGetValue(doc.Id, out var f) ? f : [];
            var stmtDate = stmtDateByDocId.TryGetValue(doc.Id, out var sd) ? sd : null;
            var snap     = snapshotByKey.TryGetValue(groupKey, out var sn) ? sn : null;

            // ── Statement aging fallback from line items ───────────────────
            decimal? fallbackCurrent = null, fallbackAging30 = null,
                     fallbackAging60 = null, fallbackAging90 = null, fallbackAging90P = null;

            var hasDedicatedAging =
                GetFieldStatic(fields, "aging_current") != null || GetFieldStatic(fields, "aging_30") != null;

            if (!hasDedicatedAging)
            {
                var stmtRef = stmtDate ?? refDate;

                var sortedDates = fields
                    .Where(fi => string.Equals(fi.FieldName, "Date", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(fi.FieldName, "lineInvoiceDate", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(fi => fi.SortOrder)
                    .Select(fi => ParseInvoiceDate(fi.CorrectedValue ?? fi.NormalizedValue ?? fi.RawValue ?? ""))
                    .Where(d => d.HasValue).Select(d => d!.Value).ToList();

                var sortedAmounts = fields
                    .Where(fi => string.Equals(fi.FieldName, "Amount", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(fi.FieldName, "lineBalance", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(fi.FieldName, "lineAmount", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(fi => fi.SortOrder)
                    .Select(fi => ParseAmt(fi.CorrectedValue ?? fi.NormalizedValue ?? fi.RawValue ?? ""))
                    .ToList();

                var sortedCredits = fields
                    .Where(fi => string.Equals(fi.FieldName, "CreditAmount", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(fi.FieldName, "creditAmount", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(fi => fi.SortOrder)
                    .Select(fi => ParseAmt(fi.CorrectedValue ?? fi.NormalizedValue ?? fi.RawValue ?? ""))
                    .ToList();

                var pairCount = Math.Min(sortedDates.Count, sortedAmounts.Count);
                if (pairCount > 0)
                {
                    fallbackCurrent = fallbackAging30 = fallbackAging60 = fallbackAging90 = fallbackAging90P = 0m;
                    for (int i = 0; i < pairCount; i++)
                    {
                        var credit  = i < sortedCredits.Count ? sortedCredits[i] : 0m;
                        var net     = sortedAmounts[i] - credit;
                        if (net == 0) continue;
                        var ageDays = (stmtRef.Date - sortedDates[i].Date).TotalDays;
                        if      (ageDays <= 30)  fallbackCurrent  += net;
                        else if (ageDays <= 60)  fallbackAging30  += net;
                        else if (ageDays <= 90)  fallbackAging60  += net;
                        else if (ageDays <= 120) fallbackAging90  += net;
                        else                     fallbackAging90P += net;
                    }
                }
            }

            // ── ERP aging from Kind 0 snapshot ────────────────────────────
            var erpCurrent  = snap?.Current      ?? 0m;
            var erpAging30  = snap?.Aging30      ?? 0m;
            var erpAging60  = snap?.Aging60      ?? 0m;
            var erpAging90  = snap?.Aging90      ?? 0m;
            var erpAging90P = snap?.Aging90Plus  ?? 0m;
            var erpTotal    = snap?.TotalOutstanding ?? 0m;
            if (erpTotal == 0m) erpTotal = erpCurrent + erpAging30 + erpAging60 + erpAging90 + erpAging90P;

            var managerId   = doc.ApprovedBy ?? doc.ReviewedBy ?? doc.UploadedBy;
            var managerName = managerNames.TryGetValue(managerId, out var n) ? n : "Unknown";

            return new
            {
                vendorLocalId     = groupKey,
                acumaticaVendorId = doc.Vendor?.AcumaticaVendorId,
                vendorName        = doc.VendorName ?? doc.Vendor?.VendorName ?? "Unknown",
                paymentTerms      = doc.Vendor?.PaymentTerms,
                managerId,
                managerName,
                statement = new
                {
                    documentId         = doc.Id,
                    documentStatus     = doc.Status.ToString(),
                    statementDate      = stmtDate,
                    current            = GetFieldStatic(fields, "aging_current") ?? fallbackCurrent,
                    aging30            = GetFieldStatic(fields, "aging_30")      ?? fallbackAging30,
                    aging60            = GetFieldStatic(fields, "aging_60")      ?? fallbackAging60,
                    aging90            = GetFieldStatic(fields, "aging_90")      ?? fallbackAging90,
                    aging90Plus        = GetFieldStatic(fields, "aging_90plus")  ?? fallbackAging90P,
                    outstandingBalance = GetFieldStatic(fields, "outstandingBalance", "OutstandingBalance"),
                    totalInvoiceAmount = GetFieldStatic(fields, "totalInvoiceAmount"),
                },
                erp = new
                {
                    current          = erpCurrent,
                    aging30          = erpAging30,
                    aging60          = erpAging60,
                    aging90          = erpAging90,
                    aging90Plus      = erpAging90P,
                    totalOutstanding = erpTotal,
                    billCount        = 0,
                    snapshotDate     = snap?.SnapshotDate,
                },
            };
        })
        .OrderBy(v => v.vendorName)
        .ToList();

        // ── 7. Portfolio summary ──────────────────────────────────────────
        var summary = new
        {
            totalCurrent     = vendors.Sum(v => (decimal)v.erp.current),
            totalAging30     = vendors.Sum(v => (decimal)v.erp.aging30),
            totalAging60     = vendors.Sum(v => (decimal)v.erp.aging60),
            totalAging90     = vendors.Sum(v => (decimal)v.erp.aging90),
            totalAging90Plus = vendors.Sum(v => (decimal)v.erp.aging90Plus),
            totalOutstanding = vendors.Sum(v => (decimal)v.erp.totalOutstanding),
            vendorCount      = vendors.Count,
            snapshotMissing  = !latestKind0Date.HasValue,
        };

        return Ok(new { asOf = refDate, summary, vendors });
    }

    /// <summary>
    /// Returns the ERP aging data for the vendor linked to a specific document.
    /// Used by the Document Detail page to show the live Acumatica outstanding balance
    /// without requiring a full validation run.
    /// </summary>
    [HttpGet("aging/document/{documentId:guid}")]
    public async Task<IActionResult> GetDocumentVendorAging(Guid documentId, CancellationToken ct)
    {
        var doc = await _db.Documents
            .Include(d => d.Vendor)
            .Include(d => d.Branch)
            .FirstOrDefaultAsync(d => d.Id == documentId, ct);

        if (doc is null)
            return NotFound();

        // Resolve vendor ID: prefer linked Vendor entity, fall back to VendorName lookup
        var vendorParam = doc.Vendor?.AcumaticaVendorId;
        if (string.IsNullOrWhiteSpace(vendorParam) && !string.IsNullOrWhiteSpace(doc.VendorName))
        {
            var localVendor = await _db.Vendors
                .FirstOrDefaultAsync(v => v.VendorName.ToLower() == doc.VendorName.ToLower().Trim(), ct);
            vendorParam = localVendor?.AcumaticaVendorId;
        }

        if (string.IsNullOrWhiteSpace(vendorParam))
            return Ok(new { available = false });

        // Branch
        var branchParam = doc.Branch?.AcumaticaBranchId ?? doc.Branch?.BranchCode ?? doc.Branch?.BranchName;

        // Statement date from OCR
        var ocrResult = await _db.OcrResults
            .Where(r => r.DocumentId == documentId)
            .Include(r => r.ExtractedFields)
            .OrderByDescending(r => r.VersionNumber)
            .FirstOrDefaultAsync(ct);

        DateTimeOffset ageDate = DateTimeOffset.UtcNow;
        var stmtDateField = ocrResult?.ExtractedFields
            .FirstOrDefault(f => string.Equals(f.FieldName, "statementDate", StringComparison.OrdinalIgnoreCase));
        if (stmtDateField is not null)
        {
            var raw = stmtDateField.CorrectedValue ?? stmtDateField.NormalizedValue ?? stmtDateField.RawValue;
            if (!string.IsNullOrWhiteSpace(raw))
            {
                if (DateTimeOffset.TryParse(raw, out var dto))
                    ageDate = dto;
                else if (DateTime.TryParseExact(raw,
                        ["dd/MM/yyyy", "d/M/yyyy", "dd.MM.yyyy", "dd-MM-yyyy"],
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None, out var dt))
                    ageDate = new DateTimeOffset(dt, TimeSpan.Zero);
            }
        }

        try
        {
            var rows = await _erp.FetchApAgingGiAsync(branchParam, vendorParam, ageDate, ct);
            var row = rows.FirstOrDefault();
            if (row is null)
                return Ok(new { available = false, vendorId = vendorParam });

            decimal GiDec(params string[] names)
            {
                foreach (var n in names)
                    if (row.TryGetValue(n, out var raw) && !string.IsNullOrWhiteSpace(raw))
                    {
                        var cleaned = Regex.Replace(raw, @"[^\d\.\-]", "");
                        if (decimal.TryParse(cleaned, System.Globalization.NumberStyles.Any,
                                System.Globalization.CultureInfo.InvariantCulture, out var v))
                            return v;
                    }
                return 0m;
            }

            var current  = GiDec("Current",  "Balance00", "CurrBalance",  "Days0000", "Bucket0");
            var aging30  = GiDec("Period1",  "Balance01", "Days0030",     "Bucket1",  "Aging30");
            var aging60  = GiDec("Period2",  "Balance02", "Days3060",     "Bucket2",  "Aging60");
            var aging90  = GiDec("Period3",  "Balance03", "Days6090",     "Bucket3",  "Aging90");
            var aging90P = GiDec("Period4",  "Balance04", "Days90Later",  "Bucket4",  "Aging90Plus");
            var total    = GiDec("TotalBalance", "Balance", "Total", "OutstandingBalance");
            if (total == 0m) total = current + aging30 + aging60 + aging90 + aging90P;

            return Ok(new
            {
                available  = true,
                vendorId   = vendorParam,
                asOf       = ageDate,
                current, aging30, aging60, aging90,
                aging90Plus = aging90P,
                totalOutstanding = total,
            });
        }
        catch
        {
            return Ok(new { available = false, vendorId = vendorParam });
        }
    }

    // ── Snapshot: manual trigger ──────────────────────────────────────────────

    /// <summary>Enqueues an AP aging snapshot capture as a background job and returns 202 immediately.</summary>
    [HttpPost("aging/snapshot")]
    public IActionResult TriggerSnapshot()
    {
        // Forward the caller's Acumatica token into the job so it can authenticate
        // without needing a service-account token (which may not be configured).
        var token = Request.Headers["X-Acumatica-Token"].FirstOrDefault();
        _jobs.Enqueue<IAgingSnapshotService>(s => s.CaptureSnapshotAsync(true, token, CancellationToken.None));
        return Accepted(new { status = "started", snapshotDate = DateOnly.FromDateTime(DateTime.UtcNow) });
    }

    // ── Snapshot: real-time capture progress ────────────────────────────────

    /// <summary>Returns the in-progress status of the running snapshot capture job (if any).</summary>
    [HttpGet("aging/snapshot/progress")]
    public IActionResult GetCaptureProgress()
    {
        var snap = _captureProgress.GetSnapshot();
        return Ok(new
        {
            phase            = snap.Phase.ToString(),
            totalVendors     = snap.TotalVendors,
            completedVendors = snap.CompletedVendors,
            startedAt        = snap.StartedAt,
        });
    }

    // ── Snapshot: read stored data ────────────────────────────────────────────

    /// <summary>Returns the most recent stored AP aging snapshot (today's or latest available).</summary>
    [HttpGet("aging/snapshot")]
    public async Task<IActionResult> GetSnapshot([FromQuery] string? branchId, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        // Kind 1 = AP aging as of today. Fall back to the most recent available.
        var latestDate = await _db.VendorAgingSnapshots
            .Where(s => s.SnapshotDate <= today && s.SnapshotKind == 1)
            .Select(s => (DateOnly?)s.SnapshotDate)
            .MaxAsync(ct);

        if (latestDate is null)
            return Ok(new { snapshotDate = (string?)null, capturedAt = (string?)null, branches = Array.Empty<object>(), summary = new { totalCurrent = 0m, totalAging30 = 0m, totalAging60 = 0m, totalAging90 = 0m, totalAging90Plus = 0m, totalOutstanding = 0m, vendorCount = 0 }, vendors = Array.Empty<object>() });

        // All Kind 1 rows for the latest date (used to derive available branches).
        var allRows = await _db.VendorAgingSnapshots
            .Where(s => s.SnapshotDate == latestDate.Value && s.SnapshotKind == 1)
            .ToListAsync(ct);

        // Distinct branches available in this snapshot, sorted. Use branchId as key.
        var branches = allRows
            .Where(s => !string.IsNullOrWhiteSpace(s.SnapshotBranchId))
            .GroupBy(s => s.SnapshotBranchId!)
            .Select(g => new { branchId = g.Key })
            .OrderBy(b => b.branchId)
            .ToList<object>();

        // Resolve the effective branch: use the requested one if present, else the first available.
        var effectiveBranch = !string.IsNullOrWhiteSpace(branchId)
            ? branchId
            : allRows.Where(s => !string.IsNullOrWhiteSpace(s.SnapshotBranchId))
                     .Select(s => s.SnapshotBranchId)
                     .OrderBy(b => b)
                     .FirstOrDefault();

        var snapshots = allRows
            .Where(s => s.SnapshotBranchId == effectiveBranch
                     && (s.Current != 0 || s.Aging30 != 0 || s.Aging60 != 0
                      || s.Aging90 != 0 || s.Aging90Plus != 0 || s.TotalOutstanding != 0))
            .OrderBy(s => s.VendorName)
            .ToList();

        var capturedAt = snapshots.Count > 0 ? snapshots.Max(s => s.CapturedAt) : (DateTimeOffset?)null;

        var vendors = snapshots.Select(s => new
        {
            vendorLocalId     = s.VendorLocalId,
            acumaticaVendorId = s.AcumaticaVendorId,
            vendorName        = s.VendorName,
            current           = s.Current,
            aging30           = s.Aging30,
            aging60           = s.Aging60,
            aging90           = s.Aging90,
            aging90Plus       = s.Aging90Plus,
            totalOutstanding  = s.TotalOutstanding,
        }).ToList();

        var summary = new
        {
            totalCurrent     = snapshots.Sum(s => s.Current),
            totalAging30     = snapshots.Sum(s => s.Aging30),
            totalAging60     = snapshots.Sum(s => s.Aging60),
            totalAging90     = snapshots.Sum(s => s.Aging90),
            totalAging90Plus = snapshots.Sum(s => s.Aging90Plus),
            totalOutstanding = snapshots.Sum(s => s.TotalOutstanding),
            vendorCount      = snapshots.Count,
        };

        return Ok(new { snapshotDate = latestDate, capturedAt, selectedBranch = effectiveBranch, branches, summary, vendors });
    }

    // ── Excel aging report ────────────────────────────────────────────────────

    /// <summary>
    /// Generates an Excel AP aging report.
    /// Base data comes from the most recent snapshot.
    /// For vendors whose latest approved statement has a statementDate after <paramref name="reportDate"/>,
    /// the aging values from that statement (OCR-extracted) are used instead of the snapshot.
    /// </summary>
    [HttpGet("aging/report")]
    public async Task<IActionResult> GetAgingReport([FromQuery] DateOnly? reportDate, CancellationToken ct)
    {
        var refDate  = reportDate ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var today    = DateOnly.FromDateTime(DateTime.UtcNow);

        // ── 1. Load snapshot (today's or most recent) ────────────────────────
        var latestDate = await _db.VendorAgingSnapshots
            .Where(s => s.SnapshotDate <= today && s.SnapshotKind == 0)
            .Select(s => (DateOnly?)s.SnapshotDate)
            .MaxAsync(ct);

        var snapshots = latestDate.HasValue
            ? await _db.VendorAgingSnapshots
                .Where(s => s.SnapshotDate == latestDate.Value && s.SnapshotKind == 0)
                .ToListAsync(ct)
            : [];

        // ── 2. Load latest approved statement per vendor with OCR aging fields ─
        var vsTypeIds = await _db.DocumentTypes
            .Where(dt => dt.Category == DocumentCategory.VendorStatement)
            .Select(dt => dt.Id)
            .ToListAsync(ct);

        var processedStatuses = new[]
        {
            DocumentStatus.PendingReview, DocumentStatus.ReviewInProgress,
            DocumentStatus.Approved, DocumentStatus.Checked, DocumentStatus.Pushed,
        };

        var stmtDocs = vsTypeIds.Count > 0
            ? await _db.Documents
                .Where(d => d.DocumentTypeId != null &&
                            vsTypeIds.Contains(d.DocumentTypeId.Value) &&
                            processedStatuses.Contains(d.Status))
                .OrderByDescending(d => d.UploadedAt)
                .ToListAsync(ct)
            : [];

        var latestStmtByVendor = stmtDocs
            .GroupBy(d => d.VendorId.HasValue
                ? d.VendorId.Value.ToString()
                : (d.VendorName ?? $"__noid_{d.Id}"))
            .ToDictionary(g => g.Key, g => g.First());

        // ── 3. OCR fields for statement docs ─────────────────────────────────
        var stmtDocIds = latestStmtByVendor.Values.Select(d => d.Id).ToList();
        var ocrResults = stmtDocIds.Count > 0
            ? await _db.OcrResults
                .Where(r => stmtDocIds.Contains(r.DocumentId))
                .Include(r => r.ExtractedFields)
                .ToListAsync(ct)
            : [];

        var fieldsByDocId = ocrResults
            .GroupBy(r => r.DocumentId)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(r => r.VersionNumber).First().ExtractedFields.ToList());

        static DateOnly? ParseDate(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            if (DateOnly.TryParse(raw, out var d)) return d;
            if (DateTime.TryParseExact(raw,
                    ["dd.MM.yyyy", "dd/MM/yyyy", "d/M/yyyy", "d.M.yyyy", "dd-MM-yyyy"],
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var dt))
                return DateOnly.FromDateTime(dt);
            return null;
        }

        static decimal? ParseDec(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var cleaned = Regex.Replace(raw, @"[^\d\.\-]", "");
            return decimal.TryParse(cleaned, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;
        }

        decimal? GetField(IList<Domain.Entities.ExtractedField> fields, params string[] names)
        {
            foreach (var name in names)
            {
                var fld = fields.FirstOrDefault(x =>
                    string.Equals(x.FieldName, name, StringComparison.OrdinalIgnoreCase));
                if (fld is null) continue;
                var val = fld.CorrectedValue ?? fld.NormalizedValue ?? fld.RawValue;
                var dec = ParseDec(val);
                if (dec.HasValue) return dec;
            }
            return null;
        }

        // ── 4. Build report rows ──────────────────────────────────────────────
        // Use snapshot as base; override with statement OCR data when statementDate > refDate
        var rows = new List<(string VendorName, decimal Current, decimal Aging30, decimal Aging60, decimal Aging90, decimal Aging90Plus, decimal Total, string Source)>();

        var allVendorKeys = snapshots.Select(s => s.VendorLocalId)
            .Union(latestStmtByVendor.Keys)
            .Distinct()
            .ToList();

        foreach (var vendorKey in allVendorKeys)
        {
            var snap = snapshots.FirstOrDefault(s => s.VendorLocalId == vendorKey);
            latestStmtByVendor.TryGetValue(vendorKey, out var stmtDoc);

            string source = "Snapshot";
            decimal cur = snap?.Current ?? 0, a30 = snap?.Aging30 ?? 0,
                    a60 = snap?.Aging60 ?? 0, a90 = snap?.Aging90 ?? 0,
                    a90p = snap?.Aging90Plus ?? 0, total = snap?.TotalOutstanding ?? 0;
            string vendorName = snap?.VendorName ?? stmtDoc?.VendorName ?? "Unknown";

            // Check if the latest statement is newer than the report date
            if (stmtDoc is not null && fieldsByDocId.TryGetValue(stmtDoc.Id, out var fields))
            {
                var stmtDateFld = fields.FirstOrDefault(x =>
                    string.Equals(x.FieldName, "statementDate", StringComparison.OrdinalIgnoreCase));
                var rawDate = stmtDateFld?.CorrectedValue ?? stmtDateFld?.NormalizedValue ?? stmtDateFld?.RawValue;
                var stmtDate = ParseDate(rawDate);

                if (stmtDate.HasValue && stmtDate.Value > refDate)
                {
                    // Use OCR-extracted aging from this statement
                    var sCur   = GetField(fields, "aging_current");
                    var s30    = GetField(fields, "aging_30");
                    var s60    = GetField(fields, "aging_60");
                    var s90    = GetField(fields, "aging_90");
                    var s90p   = GetField(fields, "aging_90plus");
                    var sTotal = GetField(fields, "outstandingBalance", "OutstandingBalance");

                    if (sCur.HasValue || s30.HasValue || s60.HasValue || s90.HasValue || s90p.HasValue)
                    {
                        cur   = sCur   ?? 0;
                        a30   = s30    ?? 0;
                        a60   = s60    ?? 0;
                        a90   = s90    ?? 0;
                        a90p  = s90p   ?? 0;
                        total = sTotal ?? (cur + a30 + a60 + a90 + a90p);
                        source = $"Statement {stmtDate.Value:dd MMM yyyy}";
                    }
                }
            }

            if (cur + a30 + a60 + a90 + a90p + total == 0 && snap is null) continue;

            rows.Add((vendorName, cur, a30, a60, a90, a90p, total, source));
        }

        rows = [.. rows.OrderBy(r => r.VendorName)];

        // ── 5. Build Excel ────────────────────────────────────────────────────
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("AP Aging");

        // Title
        ws.Cell(1, 1).Value = $"AP Aging Report — As of {refDate:dd MMM yyyy}";
        ws.Range(1, 1, 1, 9).Merge();
        ws.Cell(1, 1).Style.Font.Bold = true;
        ws.Cell(1, 1).Style.Font.FontSize = 13;

        // Summary row
        ws.Cell(2, 1).Value = $"Generated: {DateTime.UtcNow:dd MMM yyyy HH:mm} UTC";
        ws.Cell(2, 1).Style.Font.Italic = true;

        // Headers (row 4)
        string[] headers = ["Vendor Name", "Current (Not Due)", "1–30 Days", "31–60 Days", "61–90 Days", "91+ Days", "Total Outstanding", "Source"];
        for (int i = 0; i < headers.Length; i++)
            ws.Cell(4, i + 1).Value = headers[i];

        var headerRange = ws.Range(4, 1, 4, headers.Length);
        headerRange.Style.Font.Bold = true;
        headerRange.Style.Fill.BackgroundColor = XLColor.FromHtml("#2563EB");
        headerRange.Style.Font.FontColor = XLColor.White;
        headerRange.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

        // Data rows
        int row = 5;
        foreach (var (vendorName, cur, a30, a60, a90, a90p, total, src) in rows)
        {
            ws.Cell(row, 1).Value = vendorName;
            ws.Cell(row, 2).Value = cur;
            ws.Cell(row, 3).Value = a30;
            ws.Cell(row, 4).Value = a60;
            ws.Cell(row, 5).Value = a90;
            ws.Cell(row, 6).Value = a90p;
            ws.Cell(row, 7).Value = total;
            ws.Cell(row, 8).Value = src;

            for (int c = 2; c <= 7; c++)
                ws.Cell(row, c).Style.NumberFormat.Format = "#,##0.00";

            if (a90p > 0)
                ws.Cell(row, 6).Style.Font.FontColor = XLColor.Red;
            if (a90 > 0)
                ws.Cell(row, 5).Style.Font.FontColor = XLColor.OrangeRed;

            row++;
        }

        // Totals row
        ws.Cell(row, 1).Value = "TOTAL";
        ws.Cell(row, 2).Value = rows.Sum(r => r.Current);
        ws.Cell(row, 3).Value = rows.Sum(r => r.Aging30);
        ws.Cell(row, 4).Value = rows.Sum(r => r.Aging60);
        ws.Cell(row, 5).Value = rows.Sum(r => r.Aging90);
        ws.Cell(row, 6).Value = rows.Sum(r => r.Aging90Plus);
        ws.Cell(row, 7).Value = rows.Sum(r => r.Total);
        var totalsRange = ws.Range(row, 1, row, 7);
        totalsRange.Style.Font.Bold = true;
        totalsRange.Style.Fill.BackgroundColor = XLColor.FromHtml("#F1F5F9");
        for (int c = 2; c <= 7; c++)
            ws.Cell(row, c).Style.NumberFormat.Format = "#,##0.00";

        ws.Columns().AdjustToContents(4, row);
        ws.SheetView.FreezeRows(4);

        var ms = new MemoryStream();
        wb.SaveAs(ms);
        ms.Position = 0;
        return File(ms, XlsxMime, $"ap-aging-{refDate:yyyyMMdd}.xlsx");
    }

    // ── Cash Flow Forecast Export ─────────────────────────────────────────────

    /// <summary>
    /// Exports a cash flow forecast Excel combining current ERP AP aging (Kind 1 snapshot)
    /// with the outstanding balance of all approved-but-not-yet-pushed documents.
    /// </summary>
    [HttpGet("forecast/report")]
    public async Task<IActionResult> GetForecastReport(CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        // ── 1. Latest Kind 1 snapshot (current ERP aging, all vendors) ────────
        var latestKind1Date = await _db.VendorAgingSnapshots
            .Where(s => s.SnapshotDate <= today && s.SnapshotKind == 1)
            .Select(s => (DateOnly?)s.SnapshotDate)
            .MaxAsync(ct);

        var erpSnapshots = latestKind1Date.HasValue
            ? await _db.VendorAgingSnapshots
                .Where(s => s.SnapshotDate == latestKind1Date.Value && s.SnapshotKind == 1)
                .ToListAsync(ct)
            : [];

        // ── 2. Pending documents (approved/checked, not pushed) ───────────────
        var pendingStatuses = new[] { DocumentStatus.Approved, DocumentStatus.Checked };
        var pendingDocs = await _db.Documents
            .Where(d => pendingStatuses.Contains(d.Status) && d.PushedAt == null && !d.IsDeleted)
            .Include(d => d.Vendor)
            .ToListAsync(ct);

        var pendingDocIds = pendingDocs.Select(d => d.Id).ToList();
        var pendingOcr = pendingDocIds.Count > 0
            ? await _db.OcrResults
                .Where(r => pendingDocIds.Contains(r.DocumentId))
                .Include(r => r.ExtractedFields)
                .ToListAsync(ct)
            : [];

        var pendingFieldsByDocId = pendingOcr
            .GroupBy(r => r.DocumentId)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(r => r.VersionNumber).First().ExtractedFields.ToList());

        static decimal? ParseDec(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var cleaned = Regex.Replace(raw, @"[^\d\.\-]", "");
            return decimal.TryParse(cleaned, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;
        }

        static decimal? ExtractPendingAmount(IList<Domain.Entities.ExtractedField> fields)
        {
            // Priority: outstandingBalance → totalInvoiceAmount → sum of lineBalance/lineAmount
            foreach (var name in new[] { "outstandingBalance", "OutstandingBalance", "totalInvoiceAmount" })
            {
                var fld = fields.FirstOrDefault(f =>
                    string.Equals(f.FieldName, name, StringComparison.OrdinalIgnoreCase));
                if (fld is null) continue;
                var dec = ParseDec(fld.CorrectedValue ?? fld.NormalizedValue ?? fld.RawValue);
                if (dec is > 0) return dec;
            }
            // Fallback: sum line balances
            var lineSum = fields
                .Where(f => string.Equals(f.FieldName, "lineBalance", StringComparison.OrdinalIgnoreCase)
                         || string.Equals(f.FieldName, "lineAmount", StringComparison.OrdinalIgnoreCase))
                .Sum(f => ParseDec(f.CorrectedValue ?? f.NormalizedValue ?? f.RawValue) ?? 0m);
            return lineSum > 0 ? lineSum : null;
        }

        // ── 3. Aggregate pending amounts per vendor key ───────────────────────
        var pendingByKey = new Dictionary<string, (string VendorName, decimal Amount)>(StringComparer.OrdinalIgnoreCase);
        foreach (var doc in pendingDocs)
        {
            var key = doc.Vendor?.AcumaticaVendorId
                   ?? doc.VendorName
                   ?? $"__noid_{doc.Id}";
            var name = doc.VendorName ?? doc.Vendor?.VendorName ?? "Unknown";

            pendingFieldsByDocId.TryGetValue(doc.Id, out var fields);
            var amount = fields is not null ? ExtractPendingAmount(fields) ?? 0m : 0m;
            if (amount == 0m) continue;

            if (pendingByKey.TryGetValue(key, out var existing))
                pendingByKey[key] = (existing.VendorName, existing.Amount + amount);
            else
                pendingByKey[key] = (name, amount);
        }

        // ── 4. Union vendor keys from ERP + pending ───────────────────────────
        var erpByKey = erpSnapshots.ToDictionary(s => s.VendorLocalId, s => s);
        var allKeys = erpByKey.Keys.Union(pendingByKey.Keys).Distinct().ToList();

        var rows = allKeys
            .Select(key =>
            {
                erpByKey.TryGetValue(key, out var snap);
                pendingByKey.TryGetValue(key, out var pend);

                var erpCur   = snap?.Current          ?? 0m;
                var erpA30   = snap?.Aging30           ?? 0m;
                var erpA60   = snap?.Aging60           ?? 0m;
                var erpA90   = snap?.Aging90           ?? 0m;
                var erpA90p  = snap?.Aging90Plus       ?? 0m;
                var erpTotal = snap?.TotalOutstanding  ?? 0m;
                if (erpTotal == 0m) erpTotal = erpCur + erpA30 + erpA60 + erpA90 + erpA90p;

                var pending    = pend.Amount;
                var vendorName = snap?.VendorName ?? pend.VendorName ?? key;
                var grandTotal = erpTotal + pending;

                return (vendorName, erpCur, erpA30, erpA60, erpA90, erpA90p, erpTotal, pending, grandTotal);
            })
            .Where(r => r.erpTotal != 0m || r.pending != 0m)
            .OrderBy(r => r.vendorName)
            .ToList();

        // ── 5. Build Excel ────────────────────────────────────────────────────
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Cash Flow Forecast");

        ws.Cell(1, 1).Value = $"Cash Flow Forecast — As of {today:dd MMM yyyy}";
        ws.Range(1, 1, 1, 9).Merge();
        ws.Cell(1, 1).Style.Font.Bold = true;
        ws.Cell(1, 1).Style.Font.FontSize = 13;

        ws.Cell(2, 1).Value = $"Generated: {DateTime.UtcNow:dd MMM yyyy HH:mm} UTC";
        ws.Cell(2, 1).Style.Font.Italic = true;
        if (latestKind1Date.HasValue)
        {
            ws.Cell(2, 5).Value = $"ERP Snapshot: {latestKind1Date.Value:dd MMM yyyy}";
            ws.Cell(2, 5).Style.Font.Italic = true;
        }

        string[] headers = [
            "Vendor Name",
            "Current (Not Due)", "1–30 Days", "31–60 Days", "61–90 Days", "91+ Days",
            "Total ERP Outstanding",
            "Pending (Not in ERP)",
            "Grand Total",
        ];
        for (int i = 0; i < headers.Length; i++)
            ws.Cell(4, i + 1).Value = headers[i];

        var hdrRange = ws.Range(4, 1, 4, headers.Length);
        hdrRange.Style.Font.Bold = true;
        hdrRange.Style.Fill.BackgroundColor = XLColor.FromHtml("#2563EB");
        hdrRange.Style.Font.FontColor = XLColor.White;
        hdrRange.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

        int row = 5;
        foreach (var (vendorName, erpCur, erpA30, erpA60, erpA90, erpA90p, erpTotal, pending, grandTotal) in rows)
        {
            ws.Cell(row, 1).Value = vendorName;
            ws.Cell(row, 2).Value = erpCur;
            ws.Cell(row, 3).Value = erpA30;
            ws.Cell(row, 4).Value = erpA60;
            ws.Cell(row, 5).Value = erpA90;
            ws.Cell(row, 6).Value = erpA90p;
            ws.Cell(row, 7).Value = erpTotal;
            ws.Cell(row, 8).Value = pending;
            ws.Cell(row, 9).Value = grandTotal;

            for (int c = 2; c <= 9; c++)
                ws.Cell(row, c).Style.NumberFormat.Format = "#,##0.00";

            if (erpA90p > 0) ws.Cell(row, 6).Style.Font.FontColor = XLColor.Red;
            if (erpA90  > 0) ws.Cell(row, 5).Style.Font.FontColor = XLColor.OrangeRed;
            if (pending  > 0) ws.Cell(row, 8).Style.Font.FontColor = XLColor.FromHtml("#D97706");

            row++;
        }

        ws.Cell(row, 1).Value = "TOTAL";
        ws.Cell(row, 2).Value = rows.Sum(r => r.erpCur);
        ws.Cell(row, 3).Value = rows.Sum(r => r.erpA30);
        ws.Cell(row, 4).Value = rows.Sum(r => r.erpA60);
        ws.Cell(row, 5).Value = rows.Sum(r => r.erpA90);
        ws.Cell(row, 6).Value = rows.Sum(r => r.erpA90p);
        ws.Cell(row, 7).Value = rows.Sum(r => r.erpTotal);
        ws.Cell(row, 8).Value = rows.Sum(r => r.pending);
        ws.Cell(row, 9).Value = rows.Sum(r => r.grandTotal);
        var totalsRange = ws.Range(row, 1, row, 9);
        totalsRange.Style.Font.Bold = true;
        totalsRange.Style.Fill.BackgroundColor = XLColor.FromHtml("#F1F5F9");
        for (int c = 2; c <= 9; c++)
            ws.Cell(row, c).Style.NumberFormat.Format = "#,##0.00";

        ws.Columns().AdjustToContents(4, row);
        ws.SheetView.FreezeRows(4);

        var ms = new MemoryStream();
        wb.SaveAs(ms);
        ms.Position = 0;
        return File(ms, XlsxMime, $"cashflow-forecast-{today:yyyyMMdd}.xlsx");
    }

    private static object EmptyReport(DateTimeOffset refDate) => new
    {
        asOf = refDate,
        summary = new { totalCurrent = 0m, totalAging30 = 0m, totalAging60 = 0m, totalAging90 = 0m, totalAging90Plus = 0m, totalOutstanding = 0m, vendorCount = 0, snapshotMissing = true },
        vendors = Array.Empty<object>(),
    };
}
