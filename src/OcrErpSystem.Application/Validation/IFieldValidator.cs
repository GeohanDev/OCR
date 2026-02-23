using OcrErpSystem.Application.DTOs;

namespace OcrErpSystem.Application.Validation;

public interface IFieldValidator
{
    IReadOnlyList<string> SupportedErpMappingKeys { get; }
    bool RunForAllFields { get; }
    Task<FieldValidationResult> ValidateAsync(ExtractedFieldDto field, FieldMappingConfigDto config, CancellationToken ct = default);
}

public record FieldValidationResult(
    string Status,
    string? Message,
    string ValidationType,
    object? ErpReference = null);
