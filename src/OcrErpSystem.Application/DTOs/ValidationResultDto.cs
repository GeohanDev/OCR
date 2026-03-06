namespace OcrErpSystem.Application.DTOs;

public record ValidationResultDto(
    Guid Id,
    Guid DocumentId,
    Guid? ExtractedFieldId,
    string FieldName,
    string ValidationType,
    string Status,
    string? Message,
    object? ErpReference,
    string? ErpResponseField,
    DateTimeOffset ValidatedAt);
