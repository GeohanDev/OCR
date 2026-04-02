using System.Text.Json;
using Microsoft.Extensions.Logging;
using OcrSystem.Application.DTOs;
using OcrSystem.Application.FieldMapping;
using OcrSystem.Application.OCR;
using OcrSystem.Application.Storage;
using OcrSystem.Domain.Entities;
using OcrSystem.Domain.Enums;
using OcrSystem.Infrastructure.Persistence.Repositories;
using OcrSystem.OCR;

namespace OcrSystem.Infrastructure.OCR;

public class OcrPipelineService : IOcrService
{
    private readonly IImagePreprocessor _preprocessor;
    private readonly IPaddleOcrEngine _paddle;
    private readonly ITesseractOcrEngine _tesseract;
    private readonly IClaudeFieldExtractionService _claudeExtraction;
    private readonly IFieldExtractor _extractor;
    private readonly IFieldNormalizer _normalizer;
    private readonly IConfidenceScorer _scorer;
    private readonly IFileStorageService _storage;
    private readonly IFieldMappingService _fieldMapping;
    private readonly DocumentRepository _docRepo;
    private readonly OcrResultRepository _ocrRepo;
    private readonly ValidationRepository _validationRepo;
    private readonly BranchRepository _branchRepo;
    private readonly ILogger<OcrPipelineService> _logger;

    public OcrPipelineService(
        IImagePreprocessor preprocessor,
        IPaddleOcrEngine paddle,
        ITesseractOcrEngine tesseract,
        IClaudeFieldExtractionService claudeExtraction,
        IFieldExtractor extractor,
        IFieldNormalizer normalizer,
        IConfidenceScorer scorer,
        IFileStorageService storage,
        IFieldMappingService fieldMapping,
        DocumentRepository docRepo,
        OcrResultRepository ocrRepo,
        ValidationRepository validationRepo,
        BranchRepository branchRepo,
        ILogger<OcrPipelineService> logger)
    {
        _preprocessor    = preprocessor;
        _paddle          = paddle;
        _tesseract       = tesseract;
        _claudeExtraction = claudeExtraction;
        _extractor        = extractor;
        _normalizer       = normalizer;
        _scorer           = scorer;
        _storage          = storage;
        _fieldMapping     = fieldMapping;
        _docRepo          = docRepo;
        _ocrRepo          = ocrRepo;
        _validationRepo   = validationRepo;
        _branchRepo       = branchRepo;
        _logger           = logger;
    }

    private static string ResolveMimeType(string? storedMime, string storagePath)
    {
        if (!string.IsNullOrEmpty(storedMime)
            && storedMime != "application/octet-stream"
            && storedMime != "application/x-www-form-urlencoded")
            return storedMime;

        return Path.GetExtension(storagePath).ToLowerInvariant() switch
        {
            ".pdf"              => "application/pdf",
            ".png"              => "image/png",
            ".jpg" or ".jpeg"   => "image/jpeg",
            ".tif" or ".tiff"   => "image/tiff",
            _                   => storedMime ?? "application/octet-stream"
        };
    }

    public async Task<OcrPipelineResult> ProcessDocumentAsync(Guid documentId, CancellationToken ct = default)
    {
        var doc = await _docRepo.GetByIdAsync(documentId, ct)
            ?? throw new KeyNotFoundException($"Document {documentId} not found");

        doc.Status = DocumentStatus.Processing;
        await _docRepo.UpdateAsync(doc, ct);

        // Clear stale validation results from any previous OCR run so the frontend
        // query never mixes old failed results with new auto-validation results.
        await _validationRepo.DeleteByDocumentIdAsync(documentId, CancellationToken.None);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await using var fileStream = await _storage.ReadAsync(doc.StoragePath, ct);
            var mimeType = ResolveMimeType(doc.MimeType, doc.StoragePath);

            IReadOnlyList<FieldMappingConfigDto> fieldConfigs = [];
            if (doc.DocumentTypeId.HasValue)
                fieldConfigs = await _fieldMapping.GetActiveConfigAsync(doc.DocumentTypeId.Value, ct);

            // Separate manual-entry fields — they are not extracted by OCR/Claude.
            // Exception: isCheckbox fields always go through Claude even when isManualEntry is set,
            // because Claude can auto-detect payment/credit status from the document.
            var manualEntryConfigs = fieldConfigs.Where(c => c.IsManualEntry && !c.IsCheckbox).ToList();
            var ocrFieldConfigs    = fieldConfigs.Where(c => !c.IsManualEntry || c.IsCheckbox).ToList();

            // ── Step 2 & 3: Preprocess + OCR ─────────────────────────────────
            IReadOnlyList<ProcessedPageImage> pages = [];
            IReadOnlyList<RawExtractedField> rawFields;
            IReadOnlyList<OcrBlock> ocrBlocks;
            string rawText;
            string engineVersion;
            double fallbackConf;
            int pageCount;

            bool isPdf = mimeType.Contains("pdf", StringComparison.OrdinalIgnoreCase);

            // For hybrid PDFs (scanned letterhead + digital body), Poppler must render
            // all layers. For pure-digital PDFs, PdfPig text extraction is instant.
            // Also use Poppler for pure scanned PDFs where PdfPig cannot detect the
            // embedded images (no selectable text, no extractable images) — Poppler
            // renders every page to a raster image regardless of how content is stored.
            // Also use Poppler for PDFs with garbled font encoding (glyph codes offset
            // from Unicode): Poppler renders the visual glyphs correctly regardless of
            // the font's ToUnicode map, whereas PdfPig text extraction produces garbage.
            bool hasPdfImages = _paddle.IsConfigured && isPdf
                && await _preprocessor.PdfHasEmbeddedImagesAsync(fileStream, ct);
            bool hasSelectableText = !isPdf
                || await _preprocessor.PdfHasSelectableTextAsync(fileStream, ct);
            bool hasGarbledEncoding = _paddle.IsConfigured && isPdf && hasSelectableText
                && await _preprocessor.HasGarbledFontEncodingAsync(fileStream, ct);
            bool usePoppler = _paddle.IsConfigured && isPdf
                && (hasPdfImages || !hasSelectableText || hasGarbledEncoding);

            if (usePoppler)
            {
                // Garbled-encoding PDFs: render the full page via Poppler so the visual
                // glyphs are correct. PdfPig body text is skipped entirely — it would be
                // partially garbled even after the character-shift correction heuristic.
                // Hybrid/scanned PDFs without garbled encoding: only OCR the letterhead
                // image area via Poppler-crop; PdfPig supplies the digital body text.

                // Step A: crop hints (empty for garbled PDFs → full-page Poppler render).
                fileStream.Seek(0, SeekOrigin.Begin);
                var cropHints = hasGarbledEncoding
                    ? (IReadOnlyDictionary<int, int>)new Dictionary<int, int>()
                    : await _preprocessor.GetHybridPageCropsAsync(fileStream, 200, ct);

                // Step B: extract digital body text via PdfPig (skipped for garbled PDFs).
                fileStream.Seek(0, SeekOrigin.Begin);
                var digitalPages = hasGarbledEncoding
                    ? (IReadOnlyList<ProcessedPageImage>)[]
                    : await _preprocessor.PreprocessForPaddleAsync(fileStream, mimeType, ct);

                // Step C: send PDF + crop hints to Python; Poppler renders image areas
                // (or the entire page when crop hints are empty).
                fileStream.Seek(0, SeekOrigin.Begin);
                using var ms = new MemoryStream();
                await fileStream.CopyToAsync(ms, ct);
                var paddleResult = await _paddle.RecognizePdfAsync(ms.ToArray(), cropHints, ct);

                // Step D: merge PaddleOCR blocks (letterhead) + PdfPig blocks (body).
                // For garbled PDFs digitalPages is empty, so only PaddleOCR blocks are used.
                var mergedBlocks = new List<OcrBlock>(paddleResult.Blocks);
                var pdfPigByPage = digitalPages
                    .Where(p => p.PreExtractedBlocks is { Count: > 0 })
                    .ToDictionary(p => p.PageNumber, p => p.PreExtractedBlocks!);
                foreach (var blocks in pdfPigByPage.Values)
                    mergedBlocks.AddRange(blocks);

                // Build rawText: per page, OCR letterhead text first then PdfPig body text.
                // Row-bin blocks so same-row cells are joined on one line, not one word per line.
                // PaddleOCR blocks are at 200 DPI (bin=15); PdfPig words at 300 DPI (bin=22).
                static string RowBin(IEnumerable<OcrBlock> blocks, int bin) =>
                    string.Join("\n",
                        blocks.GroupBy(b => b.BoundingBox.Y / bin)
                              .OrderBy(g => g.Key)
                              .Select(g => string.Join("  ",
                                  g.OrderBy(b => b.BoundingBox.X).Select(b => b.Text))));

                var paddleByPage = paddleResult.Blocks
                    .GroupBy(b => b.Page)
                    .ToDictionary(g => g.Key, g => g.ToList());
                var allPageNums = mergedBlocks.Select(b => b.Page).Distinct().OrderBy(x => x);
                var textParts = new List<string>();
                foreach (var pn in allPageNums)
                {
                    var parts = new List<string>();
                    if (paddleByPage.TryGetValue(pn, out var ocr) && ocr.Count > 0)
                        parts.Add(RowBin(ocr, 15));
                    if (pdfPigByPage.TryGetValue(pn, out var dig) && dig.Count > 0)
                        parts.Add(RowBin(dig, 22));
                    if (parts.Count > 0)
                        textParts.Add(string.Join("\n", parts));
                }

                rawText       = string.Join("\n\n", textParts);
                ocrBlocks     = mergedBlocks;
                engineVersion = paddleResult.EngineVersion;
                fallbackConf  = paddleResult.OverallConfidence;
                pageCount     = digitalPages.Count > 0 ? digitalPages.Count
                              : (mergedBlocks.Count > 0 ? mergedBlocks.Max(b => b.Page) : 1);
            }
            else if (_paddle.IsConfigured)
            {
                // Image file — preprocess and send to PaddleOCR as before.
                pages         = await _preprocessor.PreprocessForPaddleAsync(fileStream, mimeType, ct);
                var paddleResult = await _paddle.RecognizeAsync(pages, ct);
                rawText       = paddleResult.FullText;
                ocrBlocks     = paddleResult.Blocks;
                engineVersion = paddleResult.EngineVersion;
                fallbackConf  = paddleResult.OverallConfidence;
                pageCount     = pages.Count;
            }
            else
            {
                pages         = await _preprocessor.PreprocessAsync(fileStream, mimeType, ct);
                var ocrOutput = await _tesseract.RecognizeAsync(pages, ct);
                rawText       = ocrOutput.FullText;
                ocrBlocks     = ocrOutput.Blocks;
                engineVersion = ocrOutput.EngineVersion;
                fallbackConf  = ocrOutput.OverallConfidence;
                pageCount     = pages.Count;
            }

            // ── Step 5: Structured field extraction ──────────────────────────
            // Primary path: Claude reads the full OCR text and extracts ALL
            // configured fields as structured JSON — no regex heuristics needed.
            // This handles any document layout including multi-row tables and
            // per-row checkboxes (e.g. Settled status per invoice line).
            //
            // Fallback path: FieldExtractor (regex + keyword-anchor heuristics)
            // used when no Anthropic API key is configured.
            if (_claudeExtraction.IsConfigured)
            {
                rawFields = await _claudeExtraction.ExtractFieldsAsync(rawText, ocrFieldConfigs, ct);
            }
            else
            {
                // Adapt to TesseractOutput so FieldExtractor can work with PaddleOCR blocks.
                var adapted = new TesseractOutput(rawText, ocrBlocks, fallbackConf, engineVersion);
                rawFields = _extractor.ExtractFields(adapted, ocrFieldConfigs);
            }

            // ── Field processing (common to both paths) ───────────────────────
            var extractedFields = new List<ExtractedField>();
            var fieldDtos       = new List<ExtractedFieldDto>();
            var lowConfFields   = new List<string>();

            for (int i = 0; i < rawFields.Count; i++)
            {
                var raw = rawFields[i];
                var config    = fieldConfigs.FirstOrDefault(c => c.Id == raw.FieldMappingConfigId);
                var normalized = _normalizer.Normalize(raw.RawValue, config?.ErpMappingKey, raw.FieldName);

                // PaddleOCR + Claude extraction supply per-field confidence directly.
                // Tesseract fallback uses the scorer formula (bounding-box proximity heuristic).
                var confidence = raw.ExtractionStrategy == "ClaudeExtraction" || _paddle.IsConfigured
                    ? (double)raw.StrategyConfidence
                    : _scorer.Score(raw, ocrBlocks);

                // Only flag low-confidence for mapped fields (unconfigured extras have no threshold).
                if (config is not null)
                {
                    var threshold = (double)config.ConfidenceThreshold;
                    if (confidence < threshold)
                        lowConfFields.Add(raw.FieldName);
                }

                var field = new ExtractedField
                {
                    SortOrder            = i,
                    FieldName            = raw.FieldName,
                    FieldMappingConfigId = raw.FieldMappingConfigId,
                    RawValue             = raw.RawValue,
                    NormalizedValue      = normalized,
                    Confidence           = (decimal)confidence,
                    BoundingBox          = raw.BoundingBox is not null
                        ? JsonSerializer.Serialize(new
                            { page = raw.Page, x = raw.BoundingBox.X, y = raw.BoundingBox.Y,
                              w = raw.BoundingBox.Width, h = raw.BoundingBox.Height })
                        : null
                };
                extractedFields.Add(field);

                fieldDtos.Add(new ExtractedFieldDto(
                    field.Id, field.FieldName, field.RawValue, field.NormalizedValue,
                    (double)field.Confidence,
                    raw.BoundingBox is not null
                        ? new BoundingBoxDto(raw.Page ?? 1, raw.BoundingBox.X, raw.BoundingBox.Y,
                                             raw.BoundingBox.Width, raw.BoundingBox.Height)
                        : null,
                    false, null));
            }

            // ── Pad AllowMultiple OCR columns to uniform row count ────────────
            // Claude may extract fewer values for some columns than others (e.g. row 4 of
            // lineAmount is missing). Without a DB record for that slot the cell shows "—"
            // with no way to edit or check it. Creating a null-value placeholder gives the
            // cell an ID so the user can fill it in inline or toggle the checkbox.
            {
                var multiConfigs = ocrFieldConfigs.Where(c => c.AllowMultiple).ToList();
                if (multiConfigs.Count > 0)
                {
                    int maxRows = multiConfigs
                        .Select(c => extractedFields.Count(f =>
                            f.FieldName.Equals(c.FieldName, StringComparison.OrdinalIgnoreCase)))
                        .DefaultIfEmpty(0).Max();

                    int padSort = extractedFields.Count;
                    foreach (var mc in multiConfigs)
                    {
                        int existing = extractedFields.Count(f =>
                            f.FieldName.Equals(mc.FieldName, StringComparison.OrdinalIgnoreCase));
                        for (int j = existing; j < maxRows; j++)
                        {
                            var pad = new ExtractedField
                            {
                                SortOrder            = padSort++,
                                FieldName            = mc.FieldName,
                                FieldMappingConfigId = mc.Id,
                                RawValue             = null,
                                NormalizedValue      = null,
                                Confidence           = 0m
                            };
                            extractedFields.Add(pad);
                            fieldDtos.Add(new ExtractedFieldDto(
                                pad.Id, pad.FieldName, null, null, null, null, false, null));
                        }
                    }
                }
            }

            // ── Add empty placeholder entries for manual-entry fields ──────────
            // For AllowMultiple manual-entry fields (e.g. lineSettled), create one placeholder
            // per table row so every row gets its own toggleable field instance.
            int sortOffset = extractedFields.Count;
            int tableRowCount = ocrFieldConfigs
                .Where(c => c.AllowMultiple)
                .Select(c => extractedFields.Count(f =>
                    f.FieldName.Equals(c.FieldName, StringComparison.OrdinalIgnoreCase)))
                .DefaultIfEmpty(0)
                .Max();
            foreach (var mc in manualEntryConfigs)
            {
                int count = mc.AllowMultiple && tableRowCount > 0 ? tableRowCount : 1;
                for (int j = 0; j < count; j++)
                {
                    var placeholder = new ExtractedField
                    {
                        SortOrder            = sortOffset++,
                        FieldName            = mc.FieldName,
                        FieldMappingConfigId = mc.Id,
                        RawValue             = null,
                        NormalizedValue      = null,
                        Confidence           = 1m   // confidence is irrelevant for manual fields
                    };
                    extractedFields.Add(placeholder);
                    fieldDtos.Add(new ExtractedFieldDto(
                        placeholder.Id, placeholder.FieldName, null, null, null, null, false, null));
                }
            }

            sw.Stop();
            // Only average fields that have actual extracted values — padding/null placeholder
            // fields have Confidence=0 and would unfairly drag the overall score down.
            var valuedFields = extractedFields.Where(f => !string.IsNullOrEmpty(f.RawValue)).ToList();
            var overallConf = valuedFields.Count > 0
                ? (double)valuedFields.Average(f => f.Confidence ?? 0)
                : fallbackConf;

            var ocrResult = new OcrResult
            {
                DocumentId        = documentId,
                VersionNumber     = doc.CurrentVersion,
                RawText           = rawText,
                EngineVersion     = engineVersion,
                ProcessingMs      = (int)sw.ElapsedMilliseconds,
                OverallConfidence = (decimal)overallConf,
                PageCount         = pageCount,
                CreatedAt         = DateTimeOffset.UtcNow,
                ExtractedFields   = extractedFields
            };
            await _ocrRepo.AddResultAsync(ocrResult, ct);

            doc.Status      = DocumentStatus.PendingReview;
            doc.ProcessedAt = DateTimeOffset.UtcNow;

            // Flag for re-upload when OCR produced no usable content (confidence = 0 means
            // no text was extracted at all — the scan is unreadable or the wrong file was uploaded).
            if (overallConf < 0.05 && string.IsNullOrWhiteSpace(rawText))
                doc.ReuploadRequired = true;

            // Sync document.BranchId from the extracted branch field (if present).
            // Matches ErpMappingKey = "BranchID" / "*:BranchID" (code match)
            //      OR ErpMappingKey = "Company:BranchName" / "*:BranchName" (name match).
            // GetByCodeOrNameAsync handles both cases case-insensitively.
            var branchFieldConfig = fieldConfigs.FirstOrDefault(c =>
                c.ErpMappingKey != null &&
                (c.ErpMappingKey.Equals("BranchID", StringComparison.OrdinalIgnoreCase) ||
                 c.ErpMappingKey.EndsWith(":BranchID", StringComparison.OrdinalIgnoreCase) ||
                 c.ErpMappingKey.Equals("Company:BranchName", StringComparison.OrdinalIgnoreCase) ||
                 c.ErpMappingKey.EndsWith(":BranchName", StringComparison.OrdinalIgnoreCase)));
            if (branchFieldConfig != null)
            {
                var branchField = extractedFields.FirstOrDefault(f =>
                    f.FieldMappingConfigId == branchFieldConfig.Id &&
                    !string.IsNullOrWhiteSpace(f.CorrectedValue ?? f.NormalizedValue ?? f.RawValue));
                if (branchField != null)
                {
                    var value = (branchField.CorrectedValue ?? branchField.NormalizedValue ?? branchField.RawValue)!.Trim();
                    var branch = await _branchRepo.GetByCodeOrNameAsync(value, ct);
                    if (branch != null)
                    {
                        doc.BranchId = branch.Id;
                        _logger.LogInformation("OCR: set document {Id} branch to {Code} from extracted field '{Value}'", documentId, branch.BranchCode, value);
                    }
                    else
                        _logger.LogWarning("OCR: extracted branch value '{Value}' not found in branches table", value);
                }
            }

            // Sync document.VendorName from the extracted vendorName field (if present).
            // No Acumatica call — just captures the raw extracted value so the document
            // list can display and filter by vendor immediately after OCR completes.
            var vendorNameField = extractedFields.FirstOrDefault(f =>
                f.FieldName.Equals("vendorName", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(f.CorrectedValue ?? f.NormalizedValue ?? f.RawValue));
            if (vendorNameField != null)
            {
                var extractedVendorName = (vendorNameField.CorrectedValue ?? vendorNameField.NormalizedValue ?? vendorNameField.RawValue)!.Trim();
                if (doc.VendorName != extractedVendorName)
                {
                    doc.VendorName = extractedVendorName;
                    _logger.LogInformation("OCR: set document {Id} vendor name to '{VendorName}' from extracted field", documentId, extractedVendorName);
                }
            }

            await _docRepo.UpdateAsync(doc, ct);

            _logger.LogInformation(
                "OCR complete for {Id} via {E}: {F} fields, conf={C:F2}, {L} low-conf",
                documentId, engineVersion, fieldDtos.Count, overallConf, lowConfFields.Count);

            return new OcrPipelineResult(
                ocrResult.Id, pageCount, overallConf, fieldDtos, lowConfFields, lowConfFields.Count > 0);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OCR pipeline failed for {Id}", documentId);
            doc.Status = DocumentStatus.Uploaded; // reset to Uploaded (was PendingProcess or Processing)
            // Use CancellationToken.None — the original token may already be cancelled
            // (e.g. request aborted), which would prevent the status reset from saving.
            await _docRepo.UpdateAsync(doc, CancellationToken.None);
            throw;
        }
    }

    public async Task<OcrResultDto?> GetResultAsync(Guid documentId, CancellationToken ct = default)
    {
        var result = await _ocrRepo.GetByDocumentIdAsync(documentId, ct);
        if (result is null) return null;
        return new OcrResultDto(
            result.Id, result.DocumentId, result.VersionNumber,
            result.RawText, result.EngineVersion, result.ProcessingMs,
            result.OverallConfidence.HasValue ? (double)result.OverallConfidence.Value : null,
            result.PageCount,
            result.ExtractedFields.Select(f => new ExtractedFieldDto(
                f.Id, f.FieldName, f.RawValue, f.NormalizedValue,
                f.Confidence.HasValue ? (double)f.Confidence.Value : null,
                DeserializeBoundingBox(f.BoundingBox),
                f.IsManuallyCorreected, f.CorrectedValue)).ToList(),
            result.CreatedAt);
    }

    private static BoundingBoxDto? DeserializeBoundingBox(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var doc = JsonSerializer.Deserialize<JsonElement>(json);
            return new BoundingBoxDto(
                doc.GetProperty("page").GetInt32(),
                doc.GetProperty("x").GetInt32(),
                doc.GetProperty("y").GetInt32(),
                doc.GetProperty("w").GetInt32(),
                doc.GetProperty("h").GetInt32());
        }
        catch
        {
            return null;
        }
    }

    public async Task<ExtractedFieldDto> CorrectFieldAsync(Guid extractedFieldId, string correctedValue, Guid correctedBy, CancellationToken ct = default)
    {
        var field = await _ocrRepo.GetFieldByIdAsync(extractedFieldId, ct)
            ?? throw new KeyNotFoundException($"ExtractedField {extractedFieldId} not found");
        field.IsManuallyCorreected = true;
        field.CorrectedValue = correctedValue;
        // Re-normalize the corrected value so manual edits go through the same
        // standardization as OCR-extracted values (dates → dd/MM/yyyy, amounts → F2, etc.).
        field.NormalizedValue = _normalizer.Normalize(
            correctedValue,
            field.FieldMappingConfig?.ErpMappingKey,
            field.FieldName);
        field.CorrectedBy = correctedBy;
        field.CorrectedAt = DateTimeOffset.UtcNow;
        await _ocrRepo.UpdateFieldAsync(field, ct);

        // Keep document.VendorName in sync when the vendorName OCR field is corrected.
        if (field.FieldName.Equals("vendorName", StringComparison.OrdinalIgnoreCase))
        {
            var ocrResult = await _ocrRepo.GetByIdAsync(field.OcrResultId, ct);
            if (ocrResult is not null)
            {
                var doc = await _docRepo.GetByIdAsync(ocrResult.DocumentId, ct);
                var trimmed = correctedValue?.Trim();
                if (doc is not null && doc.VendorName != trimmed)
                {
                    doc.VendorName = trimmed;
                    await _docRepo.UpdateAsync(doc, ct);
                }
            }
        }

        // Sync document.BranchId when a branch field is corrected.
        var mappingKey = field.FieldMappingConfig?.ErpMappingKey;
        if (mappingKey != null &&
            (mappingKey.Equals("BranchID", StringComparison.OrdinalIgnoreCase) ||
             mappingKey.EndsWith(":BranchID", StringComparison.OrdinalIgnoreCase) ||
             mappingKey.Equals("Company:BranchName", StringComparison.OrdinalIgnoreCase) ||
             mappingKey.EndsWith(":BranchName", StringComparison.OrdinalIgnoreCase)) &&
            !string.IsNullOrWhiteSpace(correctedValue))
        {
            var branch = await _branchRepo.GetByCodeOrNameAsync(correctedValue.Trim(), ct);
            if (branch != null)
            {
                var ocrResult2 = await _ocrRepo.GetByIdAsync(field.OcrResultId, ct);
                if (ocrResult2 is not null)
                {
                    var doc2 = await _docRepo.GetByIdAsync(ocrResult2.DocumentId, ct);
                    if (doc2 is not null)
                    {
                        doc2.BranchId = branch.Id;
                        await _docRepo.UpdateAsync(doc2, ct);
                    }
                }
            }
        }

        return new ExtractedFieldDto(
            field.Id, field.FieldName, field.RawValue, field.NormalizedValue,
            field.Confidence.HasValue ? (double)field.Confidence.Value : null,
            null, true, correctedValue);
    }

    public async Task<string?> GetRawTextAsync(Guid documentId, CancellationToken ct = default)
    {
        var result = await _ocrRepo.GetByDocumentIdAsync(documentId, ct);
        return result?.RawText;
    }

    public async Task<string> RunPaddleOcrRawAsync(Guid documentId, CancellationToken ct = default)
    {
        var doc = await _docRepo.GetByIdAsync(documentId, ct)
            ?? throw new KeyNotFoundException($"Document {documentId} not found");

        if (!_paddle.IsConfigured)
            throw new InvalidOperationException("PaddleOCR is not configured.");

        await using var fileStream = await _storage.ReadAsync(doc.StoragePath, ct);
        var mimeType = ResolveMimeType(doc.MimeType, doc.StoragePath);

        PaddleOcrOutput result;
        bool isPdfRaw = mimeType.Contains("pdf", StringComparison.OrdinalIgnoreCase);
        bool hasPdfImagesRaw = isPdfRaw && await _preprocessor.PdfHasEmbeddedImagesAsync(fileStream, ct);
        bool hasSelectableTextRaw = !isPdfRaw || await _preprocessor.PdfHasSelectableTextAsync(fileStream, ct);
        bool hasGarbledEncodingRaw = isPdfRaw && hasSelectableTextRaw
            && await _preprocessor.HasGarbledFontEncodingAsync(fileStream, ct);
        bool usePoppler = isPdfRaw && (hasPdfImagesRaw || !hasSelectableTextRaw || hasGarbledEncodingRaw);

        if (usePoppler)
        {
            fileStream.Seek(0, SeekOrigin.Begin);
            var cropHints = hasGarbledEncodingRaw
                ? (IReadOnlyDictionary<int, int>)new Dictionary<int, int>()
                : await _preprocessor.GetHybridPageCropsAsync(fileStream, 200, ct);

            fileStream.Seek(0, SeekOrigin.Begin);
            var digitalPages = hasGarbledEncodingRaw
                ? (IReadOnlyList<ProcessedPageImage>)[]
                : await _preprocessor.PreprocessForPaddleAsync(fileStream, mimeType, ct);

            fileStream.Seek(0, SeekOrigin.Begin);
            using var ms = new MemoryStream();
            await fileStream.CopyToAsync(ms, ct);
            var paddleResult = await _paddle.RecognizePdfAsync(ms.ToArray(), cropHints, ct);

            var pdfPigByPage = digitalPages
                .Where(p => p.PreExtractedBlocks is { Count: > 0 })
                .ToDictionary(p => p.PageNumber, p => p.PreExtractedBlocks!);
            var paddleByPage = paddleResult.Blocks
                .GroupBy(b => b.Page).ToDictionary(g => g.Key, g => g.ToList());
            var allPageNums = paddleResult.Blocks.Select(b => b.Page)
                .Concat(pdfPigByPage.Keys).Distinct().OrderBy(x => x);

            static string RowBinRaw(IEnumerable<OcrBlock> blocks, int bin) =>
                string.Join("\n",
                    blocks.GroupBy(b => b.BoundingBox.Y / bin)
                          .OrderBy(g => g.Key)
                          .Select(g => string.Join("  ",
                              g.OrderBy(b => b.BoundingBox.X).Select(b => b.Text))));

            var textParts = new List<string>();
            foreach (var pn in allPageNums)
            {
                var parts = new List<string>();
                if (paddleByPage.TryGetValue(pn, out var ocr) && ocr.Count > 0)
                    parts.Add(RowBinRaw(ocr, 15));
                if (pdfPigByPage.TryGetValue(pn, out var dig) && dig.Count > 0)
                    parts.Add(RowBinRaw(dig, 22));
                if (parts.Count > 0)
                    textParts.Add(string.Join("\n", parts));
            }

            result = new PaddleOcrOutput(
                string.Join("\n\n", textParts),
                paddleResult.Blocks,
                paddleResult.Tables,
                paddleResult.OverallConfidence,
                paddleResult.EngineVersion);
        }
        else
        {
            var pages = await _preprocessor.PreprocessForPaddleAsync(fileStream, mimeType, ct);
            result = await _paddle.RecognizeAsync(pages, ct);
        }
        var rawText = result.FullText;

        // Persist the updated raw text so the UI can display the latest output.
        var ocrResult = await _ocrRepo.GetByDocumentIdAsync(documentId, ct);
        if (ocrResult is not null)
        {
            ocrResult.RawText = rawText;
            await _ocrRepo.UpdateAsync(ocrResult, ct);
        }

        _logger.LogInformation("RunPaddleOcrRaw: document {DocumentId}, {Len} chars", documentId, rawText.Length);
        return rawText;
    }

    public async Task DeleteFieldAsync(Guid extractedFieldId, CancellationToken ct = default)
    {
        // Delete validation results first so counts are accurate on next server fetch.
        await _validationRepo.DeleteByExtractedFieldIdAsync(extractedFieldId, ct);
        await _ocrRepo.DeleteFieldAsync(extractedFieldId, ct);
    }

    public async Task<OcrResultDto> ReExtractFieldsAsync(Guid documentId, CancellationToken ct = default)
    {
        var doc = await _docRepo.GetByIdAsync(documentId, ct)
            ?? throw new KeyNotFoundException($"Document {documentId} not found");

        if (!doc.DocumentTypeId.HasValue)
            throw new InvalidOperationException("No document type assigned to this document.");

        var ocrResult = await _ocrRepo.GetByDocumentIdAsync(documentId, ct)
            ?? throw new InvalidOperationException("No OCR result found. Run OCR first.");

        var rawText = ocrResult.RawText ?? string.Empty;

        var fieldConfigs = await _fieldMapping.GetActiveConfigAsync(doc.DocumentTypeId.Value, ct);
        var manualEntryConfigs = fieldConfigs.Where(c => c.IsManualEntry && !c.IsCheckbox).ToList();
        var ocrFieldConfigs    = fieldConfigs.Where(c => !c.IsManualEntry || c.IsCheckbox).ToList();

        // Re-run Claude extraction on the stored raw text
        IReadOnlyList<RawExtractedField> rawFields;
        if (_claudeExtraction.IsConfigured)
            rawFields = await _claudeExtraction.ExtractFieldsAsync(rawText, ocrFieldConfigs, ct);
        else
            rawFields = ocrFieldConfigs
                .Select(c => new RawExtractedField(c.Id, c.FieldName, null, "ClaudeExtraction", 0f, null, null))
                .ToList();

        // Build new ExtractedField entities (mirrors ProcessDocumentAsync logic)
        var extractedFields = new List<ExtractedField>();
        int sort = 0;
        foreach (var raw in rawFields)
        {
            var config     = fieldConfigs.FirstOrDefault(c => c.Id == raw.FieldMappingConfigId);
            var normalized = _normalizer.Normalize(raw.RawValue, config?.ErpMappingKey, raw.FieldName);
            extractedFields.Add(new ExtractedField
            {
                OcrResultId          = ocrResult.Id,
                SortOrder            = sort++,
                FieldName            = raw.FieldName,
                FieldMappingConfigId = raw.FieldMappingConfigId,
                RawValue             = raw.RawValue,
                NormalizedValue      = normalized,
                Confidence           = (decimal)raw.StrategyConfidence,
            });
        }

        // Pad AllowMultiple columns to uniform row count (same as ProcessDocumentAsync)
        var multiConfigs = ocrFieldConfigs.Where(c => c.AllowMultiple).ToList();
        if (multiConfigs.Count > 0)
        {
            int maxRows = multiConfigs
                .Select(c => extractedFields.Count(f =>
                    f.FieldName.Equals(c.FieldName, StringComparison.OrdinalIgnoreCase)))
                .DefaultIfEmpty(0).Max();

            int padSort = sort;
            foreach (var mc in multiConfigs)
            {
                int existing = extractedFields.Count(f =>
                    f.FieldName.Equals(mc.FieldName, StringComparison.OrdinalIgnoreCase));
                for (int j = existing; j < maxRows; j++)
                {
                    extractedFields.Add(new ExtractedField
                    {
                        OcrResultId          = ocrResult.Id,
                        SortOrder            = padSort++,
                        FieldName            = mc.FieldName,
                        FieldMappingConfigId = mc.Id,
                        RawValue             = null,
                        NormalizedValue      = null,
                        Confidence           = 0m,
                    });
                }
            }
            sort = padSort;
        }

        // Add manual-entry placeholders (same as ProcessDocumentAsync)
        int tableRowCount = ocrFieldConfigs
            .Where(c => c.AllowMultiple)
            .Select(c => extractedFields.Count(f =>
                f.FieldName.Equals(c.FieldName, StringComparison.OrdinalIgnoreCase)))
            .DefaultIfEmpty(0).Max();
        foreach (var mc in manualEntryConfigs)
        {
            int count = mc.AllowMultiple && tableRowCount > 0 ? tableRowCount : 1;
            for (int j = 0; j < count; j++)
            {
                extractedFields.Add(new ExtractedField
                {
                    OcrResultId          = ocrResult.Id,
                    SortOrder            = sort++,
                    FieldName            = mc.FieldName,
                    FieldMappingConfigId = mc.Id,
                    RawValue             = null,
                    NormalizedValue      = null,
                    Confidence           = 1m,
                });
            }
        }

        // Delete all old fields (cascade deletes their validation results too via the repo)
        await _validationRepo.DeleteByDocumentIdAsync(documentId, ct);
        await _ocrRepo.DeleteAllFieldsAsync(ocrResult.Id, ct);

        // Persist new fields
        await _ocrRepo.AddFieldsAsync(extractedFields, ct);

        // Sync doc.VendorName from the extracted vendorName field (mirrors CorrectFieldAsync logic).
        var vendorField = extractedFields.FirstOrDefault(f =>
            f.FieldName.Equals("vendorName", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(f.NormalizedValue ?? f.RawValue));
        if (vendorField != null)
        {
            var extracted = (vendorField.NormalizedValue ?? vendorField.RawValue)!.Trim();
            if (doc.VendorName != extracted)
            {
                doc.VendorName = extracted;
                await _docRepo.UpdateAsync(doc, ct);
            }
        }

        _logger.LogInformation(
            "ReExtractFields for {Id}: {Count} fields from {Chars} chars of raw text",
            documentId, extractedFields.Count, rawText.Length);

        return (await GetResultAsync(documentId, ct))!;
    }

    public async Task<IReadOnlyList<ExtractedFieldDto>> AddTableRowAsync(Guid documentId, IReadOnlyList<AddTableRowColumn> columns, CancellationToken ct = default)
    {
        var ocrResult = await _ocrRepo.GetByDocumentIdAsync(documentId, ct)
            ?? throw new KeyNotFoundException($"No OCR result for document {documentId}");

        int maxSort = ocrResult.ExtractedFields.Any()
            ? ocrResult.ExtractedFields.Max(f => f.SortOrder)
            : -1;

        var added = new List<ExtractedFieldDto>();
        int sort = maxSort + 1;
        foreach (var col in columns)
        {
            var field = new ExtractedField
            {
                OcrResultId          = ocrResult.Id,
                SortOrder            = sort++,
                FieldName            = col.FieldName,
                FieldMappingConfigId = col.FieldMappingConfigId,
                RawValue             = null,
                NormalizedValue      = null,
                Confidence           = 0m,
            };
            await _ocrRepo.AddFieldAsync(field, ct);
            added.Add(new ExtractedFieldDto(field.Id, field.FieldName, null, null, null, null, false, null));
        }
        return added;
    }
}
