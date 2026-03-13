using OcrSystem.Application.DTOs;
using OcrSystem.Application.Validation;

namespace OcrSystem.Infrastructure.Validation.Validators;

public class RequiredFieldValidator : IFieldValidator
{
    public IReadOnlyList<string> SupportedErpMappingKeys => [];
    public bool RunForAllFields => true;

    public Task<FieldValidationResult> ValidateAsync(ExtractedFieldDto field, FieldMappingConfigDto config, CancellationToken ct = default)
    {
        if (!config.IsRequired)
            return Task.FromResult(new FieldValidationResult("Skipped", null, "Required"));

        var value = field.CorrectedValue ?? field.NormalizedValue ?? field.RawValue;
        return Task.FromResult(string.IsNullOrWhiteSpace(value)
            ? new FieldValidationResult("Failed", $"Required field '{field.FieldName}' is missing or empty.", "Required")
            : new FieldValidationResult("Skipped", null, "Required"));
    }
}
