using OcrErpSystem.Application.DTOs;
using OcrErpSystem.Application.ERP;
using OcrErpSystem.Application.Validation;

namespace OcrErpSystem.Infrastructure.Validation.Validators;

public class ErpCurrencyValidator : IFieldValidator
{
    private readonly IErpIntegrationService _erp;
    public ErpCurrencyValidator(IErpIntegrationService erp) => _erp = erp;
    public IReadOnlyList<string> SupportedErpMappingKeys => ["CurrencyID"];
    public bool RunForAllFields => false;

    public async Task<FieldValidationResult> ValidateAsync(ExtractedFieldDto field, FieldMappingConfigDto config, CancellationToken ct = default)
    {
        var value = (field.CorrectedValue ?? field.NormalizedValue ?? field.RawValue)?.Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(value))
            return new FieldValidationResult("Skipped", "No value to validate.", "ErpCurrency");

        var result = await _erp.LookupCurrencyAsync(value, ct);
        return result.Found
            ? new FieldValidationResult("Passed", null, "ErpCurrency", result.Data)
            : new FieldValidationResult("Failed", $"Currency '{value}' not found in ERP.", "ErpCurrency");
    }
}
