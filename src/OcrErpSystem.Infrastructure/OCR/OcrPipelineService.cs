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
    private readonly IFieldExtractor _extractor;
    private readonly IFieldNormalizer _normalizer;
    private readonly IConfidenceScorer _scorer;
    private readonly IFileStorageService _storage;
    private readonly IFieldMappingService _fieldMapping;
    private readonly DocumentRepository _docRepo;
    private readonly OcrResultRepository _ocrRepo;
    private readonly ILogger<OcrPipelineService> _logger;

    public OcrPipelineService(
        IImagePreprocessor preprocessor,
        ITesseractOcrEngine engine,
        IFieldExtractor extractor,
        IFieldNormalizer normalizer,
        IConfidenceScorer scorer,
        IFileStorageService storage,
        IFieldMappingService fieldMapping,
        DocumentRepository docRepo,
        OcrResultRepository ocrRepo,
        ILogger<OcrPipelineService> logger)
    {
        _preprocessor = preprocessor;
        _engine = engine;
        _extractor = extractor;
        _normalizer = normalizer;
        _scorer = scorer;
        _storage = storage;
        _fieldMapping = fieldMapping;
        _docRepo = docRepo;
        _ocrRepo = ocrRepo;
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
            // Resolve MIME type from storage extension when the stored value is missing or generic.
            var mimeType = ResolveMimeType(doc.MimeType, doc.StoragePath);
            var pages = await _preprocessor.PreprocessAsync(fileStream, mimeType, ct);
            var ocrOutput = await _engine.RecognizeAsync(pages, ct);

            IReadOnlyList<FieldMappingConfigDto> fieldConfigs = [];
            if (doc.DocumentTypeId.HasValue)
                fieldConfigs = await _fieldMapping.GetActiveConfigAsync(doc.DocumentTypeId.Value, ct);

            var rawFields = _extractor.ExtractFields(ocrOutput, fieldConfigs);

            var extractedFields = new List<ExtractedField>();
            var fieldDtos = new List<ExtractedFieldDto>();
            var lowConfFields = new List<string>();

            foreach (var raw in rawFields)
            {
                var config = fieldConfigs.FirstOrDefault(c => c.Id == raw.FieldMappingConfigId);
                var normalized = _normalizer.Normalize(raw.RawValue, config?.ErpMappingKey);
                var confidence = _scorer.Score(raw, ocrOutput.Blocks);
                var threshold = config?.ConfidenceThreshold ?? 0.75;

                if (confidence < threshold)
                    lowConfFields.Add(raw.FieldName);

                var field = new ExtractedField
                {
                    FieldName = raw.FieldName,
                    FieldMappingConfigId = raw.FieldMappingConfigId,
                    RawValue = raw.RawValue,
                    NormalizedValue = normalized,
                    Confidence = (decimal)confidence,
                    BoundingBox = raw.BoundingBox is not null
                        ? JsonSerializer.Serialize(new { page = raw.Page, x = raw.BoundingBox.X, y = raw.BoundingBox.Y, w = raw.BoundingBox.Width, h = raw.BoundingBox.Height })
                        : null
                };
                extractedFields.Add(field);

                fieldDtos.Add(new ExtractedFieldDto(
                    field.Id, field.FieldName, field.RawValue, field.NormalizedValue,
                    (double)field.Confidence,
                    raw.BoundingBox is not null
                        ? new BoundingBoxDto(raw.Page ?? 1, raw.BoundingBox.X, raw.BoundingBox.Y, raw.BoundingBox.Width, raw.BoundingBox.Height)
                        : null,
                    false, null));
            }

            sw.Stop();
            var overallConf = extractedFields.Count > 0 ? (double)extractedFields.Average(f => f.Confidence ?? 0) : 0;

            var ocrResult = new OcrResult
            {
                DocumentId = documentId,
                VersionNumber = doc.CurrentVersion,
                RawText = ocrOutput.FullText,
                EngineVersion = ocrOutput.EngineVersion,
                ProcessingMs = (int)sw.ElapsedMilliseconds,
                OverallConfidence = (decimal)overallConf,
                PageCount = pages.Count,
                CreatedAt = DateTimeOffset.UtcNow,
                ExtractedFields = extractedFields
            };
            await _ocrRepo.AddResultAsync(ocrResult, ct);

            doc.Status = DocumentStatus.PendingReview;
            doc.ProcessedAt = DateTimeOffset.UtcNow;
            await _docRepo.UpdateAsync(doc, ct);

            _logger.LogInformation("OCR complete for {Id}: {F} fields, conf={C:F2}, {L} low-conf",
                documentId, fieldDtos.Count, overallConf, lowConfFields.Count);

            return new OcrPipelineResult(ocrResult.Id, pages.Count, overallConf, fieldDtos, lowConfFields, lowConfFields.Count > 0);
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
                null, f.IsManuallyCorreected, f.CorrectedValue)).ToList(),
            result.CreatedAt);
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
}
