using System.Text.RegularExpressions;
using OcrSystem.Application.DTOs;
using OcrSystem.Application.Validation;

namespace OcrSystem.Infrastructure.Validation.Validators;

public class FormatValidator : IFieldValidator
{
    public IReadOnlyList<string> SupportedErpMappingKeys => [];
    public bool RunForAllFields => true;

    public Task<FieldValidationResult> ValidateAsync(ExtractedFieldDto field, FieldMappingConfigDto config, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(config.RegexPattern))
            return Task.FromResult(new FieldValidationResult("Skipped", null, "Format"));

        var value = field.CorrectedValue ?? field.NormalizedValue ?? field.RawValue;
        if (string.IsNullOrWhiteSpace(value))
            return Task.FromResult(new FieldValidationResult("Skipped", "No value to format-check.", "Format"));

        var isMatch = Regex.IsMatch(value, config.RegexPattern, RegexOptions.IgnoreCase);
        return Task.FromResult(isMatch
            ? new FieldValidationResult("Passed", null, "Format")
            : new FieldValidationResult("Warning", $"Value '{value}' does not match expected format.", "Format"));
    }
}
