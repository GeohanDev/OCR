using OcrErpSystem.Application.DTOs;

namespace OcrErpSystem.Application.OCR;

public interface IOcrService
{
    Task<OcrPipelineResult> ProcessDocumentAsync(Guid documentId, CancellationToken ct = default);
    Task<OcrResultDto?> GetResultAsync(Guid documentId, CancellationToken ct = default);
    Task<ExtractedFieldDto> CorrectFieldAsync(Guid extractedFieldId, string correctedValue, Guid correctedBy, CancellationToken ct = default);
    Task<string?> GetRawTextAsync(Guid documentId, CancellationToken ct = default);
}

public record OcrPipelineResult(
    Guid OcrResultId,
    int PageCount,
    double OverallConfidence,
    IReadOnlyList<ExtractedFieldDto> Fields,
    IReadOnlyList<string> LowConfidenceFieldNames,
    bool RequiresManualReview);
