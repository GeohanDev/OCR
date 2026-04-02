using ClosedXML.Excel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OcrSystem.Application.Auth;
using OcrSystem.Application.Storage;
using OcrSystem.Domain.Enums;
using OcrSystem.Infrastructure.Persistence;
using System.IO.Compression;

namespace OcrSystem.API.Controllers;

[ApiController]
[Route("api/export")]
[Authorize(Policy = "ManagerAndAbove")]
public class ExportController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IFileStorageService _storage;
    private readonly ICurrentUserContext _user;

    private const string XlsxMime = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    public ExportController(AppDbContext db, IFileStorageService storage, ICurrentUserContext user)
    {
        _db = db;
        _storage = storage;
        _user = user;
    }

    // ── Shared query helper ───────────────────────────────────────────────────

    private IQueryable<Domain.Entities.Document> BuildDocQuery(
        DateTimeOffset? from, DateTimeOffset? to,
        string? status, Guid? documentTypeId, string? vendorName)
    {
        var q = _db.Documents
            .Include(d => d.DocumentType)
            .Include(d => d.UploadedByUser)
            .Include(d => d.Vendor)
            .Where(d => !d.IsDeleted)
            .AsQueryable();

        bool isManager = _user.Role.Equals("Manager", StringComparison.OrdinalIgnoreCase);
        if (isManager && _user.BranchId.HasValue)
            q = q.Where(d => d.BranchId == _user.BranchId);

        if (from.HasValue) q = q.Where(d => d.UploadedAt >= from.Value);
        if (to.HasValue)   q = q.Where(d => d.UploadedAt <= to.Value);

        if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<DocumentStatus>(status, out var ds))
            q = q.Where(d => d.Status == ds);

        if (documentTypeId.HasValue)
            q = q.Where(d => d.DocumentTypeId == documentTypeId.Value);

        if (!string.IsNullOrWhiteSpace(vendorName))
            q = q.Where(d => d.VendorName != null && EF.Functions.ILike(d.VendorName, $"%{vendorName}%"));

        return q;
    }

    // ── 1. Document list Excel ────────────────────────────────────────────────

    /// <summary>Excel workbook of document metadata matching filters.</summary>
    [HttpGet("documents")]
    public async Task<IActionResult> ExportDocumentsExcel(
        [FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to,
        [FromQuery] string? status, [FromQuery] Guid? documentTypeId,
        [FromQuery] string? vendorName, CancellationToken ct)
    {
        var docs = await BuildDocQuery(from, to, status, documentTypeId, vendorName)
            .OrderByDescending(d => d.UploadedAt)
            .ToListAsync(ct);

        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Documents");

        // Headers
        string[] headers = ["ID", "Filename", "Document Type", "Status", "Vendor Name",
                             "Uploaded By", "Uploaded At", "Reviewed At", "Approved At",
                             "Pushed At", "Notes"];
        for (int i = 0; i < headers.Length; i++)
            ws.Cell(1, i + 1).Value = headers[i];

        StyleHeaderRow(ws, headers.Length);

        // Data
        int row = 2;
        foreach (var doc in docs)
        {
            ws.Cell(row, 1).Value  = doc.Id.ToString();
            ws.Cell(row, 2).Value  = doc.OriginalFilename;
            ws.Cell(row, 3).Value  = doc.DocumentType?.DisplayName ?? "";
            ws.Cell(row, 4).Value  = doc.Status.ToString();
            ws.Cell(row, 5).Value  = doc.VendorName ?? doc.Vendor?.VendorName ?? "";
            ws.Cell(row, 6).Value  = doc.UploadedByUser?.DisplayName ?? doc.UploadedByUser?.Username ?? "";
            ws.Cell(row, 7).Value  = doc.UploadedAt.ToString("yyyy-MM-dd HH:mm:ss");
            ws.Cell(row, 8).Value  = doc.ReviewedAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? "";
            ws.Cell(row, 9).Value  = doc.ApprovedAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? "";
            ws.Cell(row, 10).Value = doc.PushedAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? "";
            ws.Cell(row, 11).Value = doc.Notes ?? "";
            row++;
        }

        ws.Columns().AdjustToContents(2, row - 1);
        ws.SheetView.FreezeRows(1);

        var ms = new MemoryStream();
        wb.SaveAs(ms);
        ms.Position = 0;
        return File(ms, XlsxMime, $"documents-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.xlsx");
    }

    // ── 2. Extracted field data Excel ─────────────────────────────────────────

    /// <summary>
    /// Excel workbook of every extracted OCR field for all documents matching the filters.
    /// Each extracted field becomes one row, giving a full data dump of what the system recorded.
    /// </summary>
    [HttpGet("documents/data")]
    public async Task<IActionResult> ExportDocumentDataExcel(
        [FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to,
        [FromQuery] string? status, [FromQuery] Guid? documentTypeId,
        [FromQuery] string? vendorName, CancellationToken ct)
    {
        var docs = await BuildDocQuery(from, to, status, documentTypeId, vendorName)
            .OrderByDescending(d => d.UploadedAt)
            .Select(d => new { d.Id, d.OriginalFilename, d.Status,
                               VendorName = d.VendorName ?? d.Vendor!.VendorName,
                               DocType = d.DocumentType!.DisplayName,
                               d.UploadedAt })
            .ToListAsync(ct);

        var docIds = docs.Select(d => d.Id).ToList();

        // Fetch latest OCR result per document with its extracted fields
        var ocrResults = await _db.OcrResults
            .Where(r => docIds.Contains(r.DocumentId))
            .Include(r => r.ExtractedFields)
            .ToListAsync(ct);

        var latestByDoc = ocrResults
            .GroupBy(r => r.DocumentId)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(r => r.VersionNumber).First().ExtractedFields.ToList());

        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("OCR Data");

        // Headers
        string[] headers = ["Document ID", "Filename", "Document Type", "Vendor Name", "Status",
                             "Uploaded At", "Field Name", "Raw Value", "Normalized Value",
                             "Corrected Value", "Confidence", "Manually Entered"];
        for (int i = 0; i < headers.Length; i++)
            ws.Cell(1, i + 1).Value = headers[i];

        StyleHeaderRow(ws, headers.Length);

        int row = 2;
        foreach (var doc in docs)
        {
            var fields = latestByDoc.TryGetValue(doc.Id, out var f) ? f : [];
            if (fields.Count == 0)
            {
                // Emit one row with no field data so the document is still listed
                ws.Cell(row, 1).Value  = doc.Id.ToString();
                ws.Cell(row, 2).Value  = doc.OriginalFilename;
                ws.Cell(row, 3).Value  = doc.DocType ?? "";
                ws.Cell(row, 4).Value  = doc.VendorName ?? "";
                ws.Cell(row, 5).Value  = doc.Status.ToString();
                ws.Cell(row, 6).Value  = doc.UploadedAt.ToString("yyyy-MM-dd HH:mm:ss");
                row++;
            }
            else
            {
                foreach (var fld in fields)
                {
                    ws.Cell(row, 1).Value  = doc.Id.ToString();
                    ws.Cell(row, 2).Value  = doc.OriginalFilename;
                    ws.Cell(row, 3).Value  = doc.DocType ?? "";
                    ws.Cell(row, 4).Value  = doc.VendorName ?? "";
                    ws.Cell(row, 5).Value  = doc.Status.ToString();
                    ws.Cell(row, 6).Value  = doc.UploadedAt.ToString("yyyy-MM-dd HH:mm:ss");
                    ws.Cell(row, 7).Value  = fld.FieldName;
                    ws.Cell(row, 8).Value  = fld.RawValue ?? "";
                    ws.Cell(row, 9).Value  = fld.NormalizedValue ?? "";
                    ws.Cell(row, 10).Value = fld.CorrectedValue ?? "";
                    if (fld.Confidence.HasValue)
                        ws.Cell(row, 11).Value = fld.Confidence.Value;
                    ws.Cell(row, 12).Value = fld.IsManuallyCorreected ? "Yes" : "No";
                    row++;
                }
            }
        }

        ws.Columns().AdjustToContents(2, row - 1);
        ws.SheetView.FreezeRows(1);

        var ms = new MemoryStream();
        wb.SaveAs(ms);
        ms.Position = 0;
        return File(ms, XlsxMime, $"document-data-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.xlsx");
    }

    // ── 3. ZIP of document files organised by vendor ──────────────────────────

    /// <summary>
    /// Downloads a ZIP archive containing document files organised into
    /// subdirectories by vendor name.  Structure: {VendorName}/{OriginalFilename}.
    /// Documents with no vendor go into an "Uncategorized" folder.
    /// Limited to 150 documents per request to keep response time reasonable.
    /// </summary>
    [HttpGet("documents/zip")]
    public async Task<IActionResult> ExportZipByVendor(
        [FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to,
        [FromQuery] string? status, [FromQuery] Guid? documentTypeId,
        [FromQuery] string? vendorName, CancellationToken ct)
    {
        const int MaxDocs = 150;

        var docs = await BuildDocQuery(from, to, status, documentTypeId, vendorName)
            .OrderBy(d => d.VendorName ?? "Uncategorized")
            .ThenByDescending(d => d.UploadedAt)
            .Take(MaxDocs)
            .Select(d => new
            {
                d.Id,
                d.OriginalFilename,
                d.StoragePath,
                VendorName = d.VendorName ?? d.Vendor!.VendorName ?? "Uncategorized",
            })
            .ToListAsync(ct);

        if (docs.Count == 0)
            return NotFound("No documents matched the supplied filters.");

        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            // Track used entry names to avoid duplicate filename conflicts
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var doc in docs)
            {
                var folder  = SanitizePath(doc.VendorName);
                var fname   = SanitizePath(doc.OriginalFilename);
                var entry   = $"{folder}/{fname}";

                // Deduplicate if same filename appears twice under the same vendor
                if (!usedNames.Add(entry))
                {
                    var stem = Path.GetFileNameWithoutExtension(fname);
                    var ext  = Path.GetExtension(fname);
                    entry    = $"{folder}/{stem}_{doc.Id:N}{ext}";
                    usedNames.Add(entry);
                }

                try
                {
                    await using var fileStream = await _storage.ReadAsync(doc.StoragePath, ct);
                    var zipEntry = zip.CreateEntry(entry, CompressionLevel.NoCompression);
                    await using var entryStream = zipEntry.Open();
                    await fileStream.CopyToAsync(entryStream, ct);
                }
                catch
                {
                    // Skip files that can't be read (storage error, missing file, etc.)
                }
            }
        }

        ms.Position = 0;
        var zipFilename = $"documents-by-vendor-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.zip";
        return File(ms, "application/zip", zipFilename);
    }

    // ── 4. Single-document file URL ───────────────────────────────────────────

    /// <summary>Returns a short-lived signed URL to download a specific document's file.</summary>
    [HttpGet("documents/{id:guid}/file")]
    public async Task<IActionResult> GetDocumentFileUrl(Guid id, CancellationToken ct)
    {
        var doc = await _db.Documents.FindAsync([id], ct);
        if (doc is null || doc.IsDeleted) return NotFound();

        bool isManager = _user.Role.Equals("Manager", StringComparison.OrdinalIgnoreCase);
        if (isManager && _user.BranchId.HasValue && doc.BranchId != _user.BranchId)
            return Forbid();

        var url = await _storage.GenerateSignedUrlAsync(doc.StoragePath, TimeSpan.FromMinutes(10), ct);
        return Ok(new { url });
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void StyleHeaderRow(IXLWorksheet ws, int columnCount)
    {
        var headerRange = ws.Range(1, 1, 1, columnCount);
        headerRange.Style.Font.Bold = true;
        headerRange.Style.Fill.BackgroundColor = XLColor.FromHtml("#2563EB");
        headerRange.Style.Font.FontColor = XLColor.White;
        headerRange.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
    }

    /// <summary>Removes characters that are invalid in ZIP entry paths.</summary>
    private static string SanitizePath(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return string.IsNullOrEmpty(sanitized) ? "Uncategorized" : sanitized;
    }
}
