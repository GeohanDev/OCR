using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OcrSystem.Application.Auth;
using OcrSystem.Application.CashFlow;
using OcrSystem.Application.ERP;
using OcrSystem.Domain.Entities;
using OcrSystem.Domain.Enums;
using OcrSystem.Infrastructure.Persistence;
using System.Text.RegularExpressions;

namespace OcrSystem.Infrastructure.Services;

/// <summary>
/// Captures a daily AP aging snapshot for all vendors that have statement documents.
/// Two kinds are stored each run:
///   Kind 0 — AP aging as of each vendor's latest statement date (for statement comparison).
///   Kind 1 — AP aging as of today (for current-position view).
/// Runs as a Hangfire background job (daily at 07:00 UTC, or on-demand).
/// </summary>
public class AgingSnapshotService : IAgingSnapshotService
{
    private readonly AppDbContext _db;
    private readonly IErpIntegrationService _erp;
    private readonly ILogger<AgingSnapshotService> _logger;
    private readonly ICaptureProgressTracker _progress;
    private readonly IAcumaticaTokenContext _tokenContext;

    public AgingSnapshotService(AppDbContext db, IErpIntegrationService erp, ILogger<AgingSnapshotService> logger, ICaptureProgressTracker progress, IAcumaticaTokenContext tokenContext)
    {
        _db = db;
        _erp = erp;
        _logger = logger;
        _progress = progress;
        _tokenContext = tokenContext;
    }

    public async Task<int> CaptureSnapshotAsync(bool force = false, string? acumaticaToken = null, CancellationToken ct = default)
    {
        // When triggered manually the caller's Acumatica token is forwarded so the
        // background job can authenticate without relying on a service account.
        if (!string.IsNullOrWhiteSpace(acumaticaToken))
            _tokenContext.ForwardedToken = acumaticaToken;

        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        if (!force)
        {
            var exists = await _db.VendorAgingSnapshots.AnyAsync(s => s.SnapshotDate == today, ct);
            if (exists)
            {
                _logger.LogInformation("Aging snapshot for {Date} already exists, skipping", today);
                return 0;
            }
        }
        else
        {
            var existing = await _db.VendorAgingSnapshots
                .Where(s => s.SnapshotDate == today)
                .ToListAsync(ct);
            _db.VendorAgingSnapshots.RemoveRange(existing);
            await _db.SaveChangesAsync(ct);
        }

        var refDate  = DateTimeOffset.UtcNow;
        var todayDto = new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero);

        // ── 1. Vendor-statement document types ──────────────────────────────
        var vsTypeIds = await _db.DocumentTypes
            .Where(dt => dt.Category == DocumentCategory.VendorStatement)
            .Select(dt => dt.Id)
            .ToListAsync(ct);

        if (vsTypeIds.Count == 0)
        {
            _logger.LogWarning("No vendor statement document types configured");
            return 0;
        }

        // ── 2. Latest approved statement doc per vendor ──────────────────────
        var processedStatuses = new[]
        {
            DocumentStatus.PendingReview, DocumentStatus.ReviewInProgress,
            DocumentStatus.Approved, DocumentStatus.Checked, DocumentStatus.Pushed,
        };

        var allStatementDocs = await _db.Documents
            .Where(d => d.DocumentTypeId != null &&
                        vsTypeIds.Contains(d.DocumentTypeId.Value) &&
                        processedStatuses.Contains(d.Status))
            .Include(d => d.Vendor)
            .Include(d => d.Branch)
            .OrderByDescending(d => d.UploadedAt)
            .ToListAsync(ct);

        var latestByVendor = allStatementDocs
            .GroupBy(d => d.VendorId.HasValue
                ? d.VendorId.Value.ToString()
                : (d.VendorName ?? $"__noid_{d.Id}"))
            .Select(g => g.First())
            .ToList();

        if (latestByVendor.Count == 0)
        {
            _logger.LogWarning("No processed vendor statement documents found");
            return 0;
        }

        // ── 3. Extract statement dates from OCR fields ───────────────────────
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

        // ── 4. Vendor ID fallback for unlinked docs ──────────────────────────
        var unlinkedNames = latestByVendor
            .Where(d => d.Vendor is null && !string.IsNullOrWhiteSpace(d.VendorName))
            .Select(d => d.VendorName!.ToLower().Trim())
            .Distinct().ToList();

        Dictionary<string, string> vendorIdByName = [];
        if (unlinkedNames.Count > 0)
        {
            // Use ToListAsync + client-side GroupBy to avoid duplicate-key exceptions
            // when multiple Vendor rows share the same lowercase name.
            var vendorRows = await _db.Vendors
                .Where(v => unlinkedNames.Contains(v.VendorName.ToLower().Trim()))
                .Select(v => new { Key = v.VendorName.ToLower().Trim(), v.AcumaticaVendorId })
                .ToListAsync(ct);
            vendorIdByName = vendorRows
                .GroupBy(v => v.Key)
                .ToDictionary(g => g.Key, g => g.First().AcumaticaVendorId);
        }

        // ── Helper ────────────────────────────────────────────────────────────
        static decimal GiDec(IReadOnlyDictionary<string, string> row, params string[] names)
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

        static (decimal cur, decimal a30, decimal a60, decimal a90, decimal a90p, decimal tot)
            ExtractBuckets(IReadOnlyDictionary<string, string>? giRow)
        {
            if (giRow is null) return (0, 0, 0, 0, 0, 0);
            var cur  = GiDec(giRow, "Current",  "Balance00", "CurrBalance", "Days0000", "Bucket0");
            var a30  = GiDec(giRow, "Period1",  "Balance01", "Days0030",    "Bucket1",  "Aging30");
            var a60  = GiDec(giRow, "Period2",  "Balance02", "Days3060",    "Bucket2",  "Aging60");
            var a90  = GiDec(giRow, "Period3",  "Balance03", "Days6090",    "Bucket3",  "Aging90");
            var a90p = GiDec(giRow, "Period4",  "Balance04", "Days90Later", "Bucket4",  "Aging90Plus");
            var tot  = GiDec(giRow, "TotalBalance", "Balance", "Total", "OutstandingBalance");
            if (tot == 0m) tot = cur + a30 + a60 + a90 + a90p;
            return (cur, a30, a60, a90, a90p, tot);
        }

        var capturedAt = DateTimeOffset.UtcNow;
        // ── 5. Capture Kind 0 in parallel batches (3 concurrent ERP calls) ────
        // Kind 1 is captured in a single all-vendor ERP call (see below).
        _progress.Begin(latestByVendor.Count);
        _progress.BeginPass("Statement-date aging", latestByVendor.Count);
        _progress.SetPhase(CapturePhase.FetchingVendorAging);
        using var sem = new SemaphoreSlim(3, 3);
        var bag = new System.Collections.Concurrent.ConcurrentBag<VendorAgingSnapshot>();

        IEnumerable<Task> MakeKind0Tasks() => latestByVendor.Select(async doc =>
        {
            await sem.WaitAsync(ct);
            try
            {
                var groupKey    = doc.VendorId.HasValue
                    ? doc.VendorId.Value.ToString()
                    : (doc.VendorName ?? $"__noid_{doc.Id}");
                var branchParam = doc.Branch?.AcumaticaBranchId ?? doc.Branch?.BranchCode ?? doc.Branch?.BranchName;
                var vendorParam = doc.Vendor?.AcumaticaVendorId;
                if (string.IsNullOrWhiteSpace(vendorParam) && !string.IsNullOrWhiteSpace(doc.VendorName))
                    vendorIdByName.TryGetValue(doc.VendorName.ToLower().Trim(), out vendorParam);

                var stmtDateDto  = stmtDateByDocId.TryGetValue(doc.Id, out var sd) ? sd ?? refDate : refDate;
                var stmtDateOnly = stmtDateDto == refDate ? (DateOnly?)null : DateOnly.FromDateTime(stmtDateDto.UtcDateTime);

                decimal cur = 0, a30 = 0, a60 = 0, a90 = 0, a90p = 0, tot = 0;
                try
                {
                    var rows = await _erp.FetchApAgingGiAsync(branchParam, vendorParam, stmtDateDto, ct);
                    (cur, a30, a60, a90, a90p, tot) = ExtractBuckets(rows.FirstOrDefault());
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "ERP aging (stmt-date) call failed for vendor {Name}, storing zeros", doc.VendorName);
                }

                bag.Add(new VendorAgingSnapshot
                {
                    Id                = Guid.NewGuid(),
                    VendorLocalId     = groupKey,
                    AcumaticaVendorId = doc.Vendor?.AcumaticaVendorId,
                    VendorName        = doc.VendorName ?? doc.Vendor?.VendorName ?? "Unknown",
                    SnapshotDate      = today,
                    SnapshotKind      = 0,
                    StatementDate     = stmtDateOnly,
                    Current           = cur,
                    Aging30           = a30,
                    Aging60           = a60,
                    Aging90           = a90,
                    Aging90Plus       = a90p,
                    TotalOutstanding  = tot,
                    CapturedAt        = capturedAt,
                });
                _progress.AdvanceVendor();
            }
            finally { sem.Release(); }
        });

        // Run Kind 0 (per-vendor, needs each vendor's statement date).
        await Task.WhenAll(MakeKind0Tasks());

        // ── Kind 1: current AP aging — AllAPAging GI per active branch, $top=2000 ──
        // GET without $top triggers Acumatica's optimized-export which fails on BQL-delegate GIs.
        // $top=2000 bypasses that path and returns per-vendor rows directly.
        _progress.SetPhase(CapturePhase.FetchingOpenBills);

        static string GiStr(IReadOnlyDictionary<string, string> row, params string[] names)
        {
            foreach (var n in names)
                if (row.TryGetValue(n, out var v) && !string.IsNullOrWhiteSpace(v))
                    return v.Trim();
            return "";
        }

        var activeBranches = await _db.Branches
            .Where(b => b.IsActive && b.AcumaticaBranchId != null && b.AcumaticaBranchId != "")
            .Select(b => new { b.AcumaticaBranchId, b.BranchName })
            .ToListAsync(ct);

        foreach (var branch in activeBranches)
        {
            try
            {
                var branchRows = await _erp.FetchAllApAgingFromAllGiAsync(branch.AcumaticaBranchId!, refDate, ct);
                _logger.LogInformation("Kind 1 AllAPAging branch {Branch} returned {Count} rows", branch.AcumaticaBranchId, branchRows.Count);

                foreach (var row in branchRows)
                {
                    var acuVendorId = GiStr(row, "Vendor", "AcctCD", "VendorID", "VendorId");
                    var vendorName  = GiStr(row, "AcctName", "VendorName", "Name");
                    if (string.IsNullOrWhiteSpace(acuVendorId) || acuVendorId == "{}") continue;
                    var (cur, a30, a60, a90, a90p, tot) = ExtractBuckets(row);
                    if (cur == 0 && a30 == 0 && a60 == 0 && a90 == 0 && a90p == 0 && tot == 0) continue;

                    bag.Add(new VendorAgingSnapshot
                    {
                        Id                = Guid.NewGuid(),
                        VendorLocalId     = acuVendorId,
                        AcumaticaVendorId = acuVendorId,
                        VendorName        = vendorName.Length > 0 ? vendorName : acuVendorId,
                        SnapshotDate      = today,
                        SnapshotKind      = 1,
                        SnapshotBranchId  = branch.AcumaticaBranchId,
                        StatementDate     = null,
                        Current           = cur,
                        Aging30           = a30,
                        Aging60           = a60,
                        Aging90           = a90,
                        Aging90Plus       = a90p,
                        TotalOutstanding  = tot,
                        CapturedAt        = capturedAt,
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Kind 1 AllAPAging call failed for branch {Branch} — skipping", branch.AcumaticaBranchId);
            }
        }

        var kind1Count = bag.Count(s => s.SnapshotKind == 1);
        var kind1Branches = bag.Where(s => s.SnapshotKind == 1).Select(s => s.SnapshotBranchId).Distinct().Count();
        _logger.LogInformation("Kind 1: {Vendors} rows across {Branches} branches", kind1Count, kind1Branches);

        var snapshots = bag.ToList();

        _progress.SetPhase(CapturePhase.Saving);
        _db.VendorAgingSnapshots.AddRange(snapshots);
        await _db.SaveChangesAsync(ct);
        _progress.SetPhase(CapturePhase.Done);

        _logger.LogInformation("Aging snapshot captured for {Date}: {Kind0} Kind-0 + {Kind1} Kind-1 = {Total} rows",
            today, latestByVendor.Count, kind1Count, snapshots.Count);
        return snapshots.Count;
    }
}
