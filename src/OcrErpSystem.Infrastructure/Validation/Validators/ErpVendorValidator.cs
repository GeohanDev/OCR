using OcrErpSystem.Application.DTOs;
using OcrErpSystem.Application.ERP;
using OcrErpSystem.Application.Validation;

namespace OcrErpSystem.Infrastructure.Validation.Validators;

public class ErpVendorValidator : IFieldValidator
{
    private readonly IErpIntegrationService _erp;
    public ErpVendorValidator(IErpIntegrationService erp) => _erp = erp;
    public IReadOnlyList<string> SupportedErpMappingKeys => ["VendorID"];
    public bool RunForAllFields => false;

    public async Task<FieldValidationResult> ValidateAsync(ExtractedFieldDto field, FieldMappingConfigDto config, CancellationToken ct = default)
    {
        var value = (field.CorrectedValue ?? field.NormalizedValue ?? field.RawValue)?.Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(value))
            return new FieldValidationResult("Skipped", "No value to validate.", "ErpVendor");

        var result = await _erp.LookupVendorAsync(value, ct);
        return result.Found
            ? new FieldValidationResult("Passed", null, "ErpVendor", result.Data)
            : new FieldValidationResult("Failed", $"Vendor '{value}' not found in ERP.", "ErpVendor");
    }
}
