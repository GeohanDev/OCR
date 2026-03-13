namespace OcrSystem.Application.DTOs;

public record OcrResultDto(
    Guid Id,
    Guid DocumentId,
    int VersionNumber,
    string? RawText,
    string? EngineVersion,
    int? ProcessingMs,
    double? OverallConfidence,
    int? PageCount,
    IReadOnlyList<ExtractedFieldDto> Fields,
    DateTimeOffset CreatedAt);

public record ExtractedFieldDto(
    Guid Id,
    string FieldName,
    string? RawValue,
    string? NormalizedValue,
    double? Confidence,
    BoundingBoxDto? BoundingBox,
    bool IsManuallyCorreected,
    string? CorrectedValue);

public record BoundingBoxDto(int Page, int X, int Y, int W, int H);
