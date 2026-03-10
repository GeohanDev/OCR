using OcrErpSystem.Application.DTOs;
using OcrErpSystem.Application.ERP;
using OcrErpSystem.Application.Validation;

namespace OcrErpSystem.Infrastructure.Validation.Validators;

public class ErpVendorNameValidator : IFieldValidator
{
    private readonly IErpIntegrationService _erp;
    private readonly IOwnCompanyService _ownCompany;
    private readonly IVendorResolutionContext _vendorContext;

    public ErpVendorNameValidator(IErpIntegrationService erp, IOwnCompanyService ownCompany, IVendorResolutionContext vendorContext)
    {
        _erp = erp;
        _ownCompany = ownCompany;
        _vendorContext = vendorContext;
    }

    public IReadOnlyList<string> SupportedErpMappingKeys => ["VendorName"];
    public bool RunForAllFields => false;

    public async Task<FieldValidationResult> ValidateAsync(ExtractedFieldDto field, FieldMappingConfigDto config, CancellationToken ct = default)
    {
        var value = (field.CorrectedValue ?? field.NormalizedValue ?? field.RawValue)?.Trim();
        if (string.IsNullOrWhiteSpace(value))
            return new FieldValidationResult("Skipped", "No value to validate.", "ErpVendorName");

        // OCR sometimes captures the recipient (our company) instead of the sender (vendor).
        // The own-company list is configured via OwnCompany:Names / OWN_COMPANY_NAMES env var.
        if (_ownCompany.IsOwnCompanyName(value))
        {
            _vendorContext.VendorValidationFailed = true;
            return new FieldValidationResult("Failed",
                $"'{value}' is your own company name, not the vendor. Please correct this field.",
                "ErpVendorName");
        }

        var result = await _erp.LookupVendorByNameAsync(value, ct);
        if (!result.Found)
        {
            _vendorContext.VendorValidationFailed = true;
            return new FieldValidationResult("Failed", $"Vendor '{value}' not found in Acumatica.", "ErpVendorName");
        }
        if (!result.Data!.IsActive)
            return new FieldValidationResult("Warning", $"Vendor '{value}' found (ID: {result.Data.VendorId}) but is inactive.", "ErpVendorName", result.Data);

        // Store resolved VendorId so ErpApInvoiceValidator can cross-check the invoice belongs to this vendor.
        _vendorContext.ResolvedVendorId = result.Data.VendorId;

        return new FieldValidationResult("Passed", $"Vendor verified — ID: {result.Data.VendorId}", "ErpVendorName", result.Data);
    }
}
