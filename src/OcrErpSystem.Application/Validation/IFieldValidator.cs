using OcrErpSystem.Application.DTOs;

namespace OcrErpSystem.Application.Validation;

public interface IFieldValidator
{
    IReadOnlyList<string> SupportedErpMappingKeys { get; }
    bool RunForAllFields { get; }
    Task<FieldValidationResult> ValidateAsync(ExtractedFieldDto field, FieldMappingConfigDto config, CancellationToken ct = default);

    // Default: matches when ErpMappingKey is in SupportedErpMappingKeys.
    // Override this in DynamicErpValidator to handle "Entity:Field" format.
    bool CanHandle(string erpMappingKey) =>
        SupportedErpMappingKeys.Contains(erpMappingKey, StringComparer.OrdinalIgnoreCase);
}

public record FieldValidationResult(
    string Status,
    string? Message,
    string ValidationType,
    object? ErpReference = null);
