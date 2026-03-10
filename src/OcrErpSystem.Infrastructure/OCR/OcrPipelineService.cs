using System.Text.Json;
using Microsoft.Extensions.Logging;
using OcrErpSystem.Application.DTOs;
using OcrErpSystem.Application.FieldMapping;
using OcrErpSystem.Application.OCR;
using OcrErpSystem.Application.Storage;
using OcrErpSystem.Domain.Entities;
using OcrErpSystem.Domain.Enums;
using OcrErpSystem.Infrastructure.Persistence.Repositories;
using OcrErpSystem.OCR;

namespace OcrErpSystem.Infrastructure.OCR;

public class OcrPipelineService : IOcrService
{
    private readonly IImagePreprocessor _preprocessor;
    private readonly ITesseractOcrEngine _engine;
    private readonly IClaudeOcrEngine _claude;
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
        ITesseractOcrEngine engine,
        IClaudeOcrEngine claude,
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
        _preprocessor = preprocessor;
        _engine = engine;
        _claude = claude;
        _extractor = extractor;
        _normalizer = normalizer;
        _scorer = scorer;
        _storage = storage;
        _fieldMapping = fieldMapping;
        _docRepo = docRepo;
        _ocrRepo = ocrRepo;
        _validationRepo = validationRepo;
        _logger = logger;
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

            // ── Route to Claude or Tesseract based on configuration ───────────
            IReadOnlyList<ProcessedPageImage> pages;
            IReadOnlyList<RawExtractedField> rawFields;
            IReadOnlyList<OcrBlock> ocrBlocks = [];   // only populated on Tesseract path
            string rawText;
            string engineVersion;
            double fallbackConf;   // used when no fields were extracted

            if (_claude.IsConfigured)
            {
                pages = await _preprocessor.PreprocessForClaudeAsync(fileStream, mimeType, ct);
                var claudeResult = await _claude.ExtractAsync(pages, ocrFieldConfigs, ct);
                rawText       = claudeResult.FullText;
                rawFields     = claudeResult.Fields;
                engineVersion = $"Claude/{claudeResult.ModelUsed}";
                fallbackConf  = claudeResult.OverallConfidence;
            }
            else
            {
                pages = await _preprocessor.PreprocessAsync(fileStream, mimeType, ct);
                var ocrOutput = await _engine.RecognizeAsync(pages, ct);
                rawText       = ocrOutput.FullText;
                rawFields     = _extractor.ExtractFields(ocrOutput, ocrFieldConfigs);
                ocrBlocks     = ocrOutput.Blocks;
                engineVersion = ocrOutput.EngineVersion;
                fallbackConf  = ocrOutput.OverallConfidence;
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

                // Claude supplies per-field confidence directly; Tesseract uses the scorer formula.
                var confidence = _claude.IsConfigured
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
            var overallConf = extractedFields.Count > 0
                ? (double)extractedFields.Average(f => f.Confidence ?? 0)
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
            await _docRepo.UpdateAsync(doc, ct);
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

    public async Task DeleteFieldAsync(Guid extractedFieldId, CancellationToken ct = default)
    {
        // Delete validation results first so counts are accurate on next server fetch.
        await _validationRepo.DeleteByExtractedFieldIdAsync(extractedFieldId, ct);
        await _ocrRepo.DeleteFieldAsync(extractedFieldId, ct);
    }
}
