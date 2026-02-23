using OcrErpSystem.Application.DTOs;
using OcrErpSystem.Application.ERP;
using OcrErpSystem.Application.Validation;

namespace OcrErpSystem.Infrastructure.Validation.Validators;

public class ErpBranchValidator : IFieldValidator
{
    private readonly IErpIntegrationService _erp;
    public ErpBranchValidator(IErpIntegrationService erp) => _erp = erp;
    public IReadOnlyList<string> SupportedErpMappingKeys => ["BranchID"];
    public bool RunForAllFields => false;

    public async Task<FieldValidationResult> ValidateAsync(ExtractedFieldDto field, FieldMappingConfigDto config, CancellationToken ct = default)
    {
        var value = (field.CorrectedValue ?? field.NormalizedValue ?? field.RawValue)?.Trim();
        if (string.IsNullOrWhiteSpace(value))
            return new FieldValidationResult("Skipped", "No value to validate.", "ErpBranch");

        var result = await _erp.LookupBranchAsync(value, ct);
        return result.Found
            ? new FieldValidationResult("Passed", null, "ErpBranch", result.Data)
            : new FieldValidationResult("Failed", $"Branch '{value}' not found in ERP.", "ErpBranch");
    }
}
