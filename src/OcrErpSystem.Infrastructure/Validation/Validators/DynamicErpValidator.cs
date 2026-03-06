using OcrErpSystem.Application.DTOs;
using OcrErpSystem.Application.ERP;
using OcrErpSystem.Application.Validation;

namespace OcrErpSystem.Infrastructure.Validation.Validators;

/// <summary>
/// Handles any ERP mapping key in "Entity:Field" format (e.g. "Vendor:VendorName").
/// Dynamically queries the Acumatica OData endpoint and checks if a matching record exists.
/// </summary>
public class DynamicErpValidator : IFieldValidator
{
    private readonly IErpIntegrationService _erp;
    public DynamicErpValidator(IErpIntegrationService erp) => _erp = erp;

    public IReadOnlyList<string> SupportedErpMappingKeys => [];
    public bool RunForAllFields => false;

    // Only handles keys that contain ':' — "Entity:Field" format.
    public bool CanHandle(string erpMappingKey) => erpMappingKey.Contains(':');

    public async Task<FieldValidationResult> ValidateAsync(
        ExtractedFieldDto field, FieldMappingConfigDto config, CancellationToken ct = default)
    {
        var key = config.ErpMappingKey ?? string.Empty;
        var parts = key.Split(':', 2);
        if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]))
            return new FieldValidationResult("Warning", $"Invalid ERP mapping key format '{key}'. Expected 'Entity:Field'.", "DynamicErp");

        var entity = parts[0].Trim();
        var matchField = parts[1].Trim();
        var value = (field.CorrectedValue ?? field.NormalizedValue ?? field.RawValue)?.Trim();

        if (string.IsNullOrWhiteSpace(value))
            return new FieldValidationResult("Skipped", "No value to validate.", "DynamicErp");

        // Vendor:VendorName — use the dedicated case-insensitive name lookup (same as ErpVendorNameValidator)
        // instead of OData $filter, which is case-sensitive on VendorName in most Acumatica versions.
        if (string.Equals(entity, "Vendor", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(matchField, "VendorName", StringComparison.OrdinalIgnoreCase))
        {
            var vendorResult = await _erp.LookupVendorByNameAsync(value, ct);
            if (!vendorResult.Found)
                return new FieldValidationResult("Failed",
                    $"Vendor '{value}' not found in Acumatica.", "DynamicErp");
            if (!vendorResult.Data!.IsActive)
                return new FieldValidationResult("Warning",
                    $"Vendor '{value}' found (ID: {vendorResult.Data.VendorId}) but is inactive.", "DynamicErp", vendorResult.Data);
            return new FieldValidationResult("Passed",
                $"Vendor verified — ID: {vendorResult.Data.VendorId}", "DynamicErp", vendorResult.Data);
        }

        var result = await _erp.LookupGenericAsync(entity, matchField, value, ct);

        if (!result.Found)
            return new FieldValidationResult("Failed",
                $"'{value}' not found in Acumatica {entity}.{matchField}. {result.ErrorMessage}",
                "DynamicErp");

        // Prefer a stable key field (RefNbr for invoices/bills) in the success message
        // so the user sees the document reference number, not the search field value.
        string successMsg;
        if (result.Data != null &&
            result.Data.TryGetValue("RefNbr", out var refNbr) &&
            !string.IsNullOrWhiteSpace(refNbr))
            successMsg = $"Invoice verified — RefNbr: {refNbr}";
        else
            successMsg = $"Verified in {entity} — {matchField}: {value}";

        return new FieldValidationResult("Passed", successMsg, "DynamicErp", result.Data);
    }
}
