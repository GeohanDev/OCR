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

            // ── Step 2: Preprocess ────────────────────────────────────────────
            // PaddleOCR: soft-greyscale, no binary threshold — table border lines
            //            must remain as grey pixels for PP-Structure to detect cells.
            // Tesseract: binary threshold improves recognition on low-contrast scans.
            IReadOnlyList<ProcessedPageImage> pages = _paddle.IsConfigured
                ? await _preprocessor.PreprocessForPaddleAsync(fileStream, mimeType, ct)
                : await _preprocessor.PreprocessAsync(fileStream, mimeType, ct);

            // ── Step 3: OCR Engine (PaddleOCR primary → Tesseract fallback) ──
            // Produces raw text + bounding-box blocks for every page.
            IReadOnlyList<RawExtractedField> rawFields;
            IReadOnlyList<OcrBlock> ocrBlocks;
            string rawText;
            string engineVersion;
            double fallbackConf;

            if (_paddle.IsConfigured)
            {
                var paddleResult = await _paddle.RecognizeAsync(pages, ct);
                rawText       = paddleResult.FullText;
                ocrBlocks     = paddleResult.Blocks;
                engineVersion = paddleResult.EngineVersion;
                fallbackConf  = paddleResult.OverallConfidence;
            }
            else
            {
                var ocrOutput = await _tesseract.RecognizeAsync(pages, ct);
                rawText       = ocrOutput.FullText;
                ocrBlocks     = ocrOutput.Blocks;
                engineVersion = ocrOutput.EngineVersion;
                fallbackConf  = ocrOutput.OverallConfidence;
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
                PageCount         = pages.Count,
                CreatedAt         = DateTimeOffset.UtcNow,
                ExtractedFields   = extractedFields
            };
            await _ocrRepo.AddResultAsync(ocrResult, ct);

            doc.Status      = DocumentStatus.PendingReview;
            doc.ProcessedAt = DateTimeOffset.UtcNow;
            await _docRepo.UpdateAsync(doc, ct);

            _logger.LogInformation(
                "OCR complete for {Id} via {E}: {F} fields, conf={C:F2}, {L} low-conf",
                documentId, engineVersion, fieldDtos.Count, overallConf, lowConfFields.Count);

            return new OcrPipelineResult(
                ocrResult.Id, pages.Count, overallConf, fieldDtos, lowConfFields, lowConfFields.Count > 0);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OCR pipeline failed for {Id}", documentId);
            doc.Status = DocumentStatus.Uploaded;
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

        var pages   = await _preprocessor.PreprocessForPaddleAsync(fileStream, mimeType, ct);
        var result  = await _paddle.RecognizeAsync(pages, ct);
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
}
