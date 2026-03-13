using OcrSystem.Application.DTOs;
using OcrSystem.Application.ERP;
using OcrSystem.Application.Validation;

namespace OcrSystem.Infrastructure.Validation.Validators;

public class ErpBranchValidator : IFieldValidator
{
    private readonly IErpIntegrationService _erp;
    public ErpBranchValidator(IErpIntegrationService erp) => _erp = erp;
    public IReadOnlyList<string> SupportedErpMappingKeys => ["BranchID", "Company:BranchID"];
    public bool RunForAllFields => false;

    public async Task<FieldValidationResult> ValidateAsync(ExtractedFieldDto field, FieldMappingConfigDto config, CancellationToken ct = default)
    {
        var value = (field.CorrectedValue ?? field.NormalizedValue ?? field.RawValue)?.Trim();
        if (string.IsNullOrWhiteSpace(value))
            return new FieldValidationResult("Skipped", "No value to validate.", "ErpBranch");

        var result = await _erp.LookupBranchAsync(value, ct);
        if (!result.Found)
            return new FieldValidationResult("Failed", $"Branch '{value}' not found in Acumatica.", "ErpBranch");

        var branch = result.Data!;
        if (!branch.IsActive)
            return new FieldValidationResult("Warning",
                $"Branch '{branch.BranchId}' ({branch.BranchName}) found but is inactive.", "ErpBranch", result.Data);

        return new FieldValidationResult("Passed",
            $"Branch verified — ID: {branch.BranchId}, Name: {branch.BranchName}", "ErpBranch", result.Data);
    }
}
