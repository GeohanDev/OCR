using OcrSystem.Application.DTOs;
using OcrSystem.Domain.Enums;

namespace OcrSystem.Application.Validation;

public interface IFieldValidator
{
    IReadOnlyList<string> SupportedErpMappingKeys { get; }
    bool RunForAllFields { get; }

    /// <summary>
    /// When non-null, this validator only runs for documents whose document type has this category.
    /// Null means the validator applies to all document categories.
    /// </summary>
    DocumentCategory? RequiresCategory => null;

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
