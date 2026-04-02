using OcrSystem.Application.DTOs;

namespace OcrSystem.Application.OCR;

public interface IOcrService
{
    Task<OcrPipelineResult> ProcessDocumentAsync(Guid documentId, CancellationToken ct = default);
    Task<OcrResultDto?> GetResultAsync(Guid documentId, CancellationToken ct = default);
    Task<ExtractedFieldDto> CorrectFieldAsync(Guid extractedFieldId, string correctedValue, Guid correctedBy, CancellationToken ct = default);
    Task<string?> GetRawTextAsync(Guid documentId, CancellationToken ct = default);
    /// <summary>
    /// Runs only the PaddleOCR engine on the document and updates the stored RawText.
    /// Does NOT re-extract fields, re-run Claude, or change document status.
    /// </summary>
    Task<string> RunPaddleOcrRawAsync(Guid documentId, CancellationToken ct = default);
    Task DeleteFieldAsync(Guid extractedFieldId, CancellationToken ct = default);
    Task<IReadOnlyList<ExtractedFieldDto>> AddTableRowAsync(Guid documentId, IReadOnlyList<AddTableRowColumn> columns, CancellationToken ct = default);
    /// <summary>
    /// Re-runs only Claude field extraction on the existing stored raw text.
    /// Deletes all current extracted fields and replaces them with fresh results.
    /// Does NOT re-run PaddleOCR — raw text is reused as-is.
    /// </summary>
    Task<OcrResultDto> ReExtractFieldsAsync(Guid documentId, CancellationToken ct = default);
}

public record AddTableRowColumn(string FieldName, Guid? FieldMappingConfigId);

public record OcrPipelineResult(
    Guid OcrResultId,
    int PageCount,
    double OverallConfidence,
    IReadOnlyList<ExtractedFieldDto> Fields,
    IReadOnlyList<string> LowConfidenceFieldNames,
    bool RequiresManualReview);
