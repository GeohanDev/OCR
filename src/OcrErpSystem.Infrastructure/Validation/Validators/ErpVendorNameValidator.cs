using Microsoft.Extensions.Configuration;
using OcrErpSystem.Application.DTOs;
using OcrErpSystem.Application.ERP;
using OcrErpSystem.Application.Validation;

namespace OcrErpSystem.Infrastructure.Validation.Validators;

public class ErpVendorNameValidator : IFieldValidator
{
    private readonly IErpIntegrationService _erp;
    private readonly IConfiguration _config;
    private readonly IVendorResolutionContext _vendorContext;

    public ErpVendorNameValidator(IErpIntegrationService erp, IConfiguration config, IVendorResolutionContext vendorContext)
    {
        _erp = erp;
        _config = config;
        _vendorContext = vendorContext;
    }

    public IReadOnlyList<string> SupportedErpMappingKeys => ["VendorName"];
    public bool RunForAllFields => false;

    public async Task<FieldValidationResult> ValidateAsync(ExtractedFieldDto field, FieldMappingConfigDto config, CancellationToken ct = default)
    {
        var value = (field.CorrectedValue ?? field.NormalizedValue ?? field.RawValue)?.Trim();
        if (string.IsNullOrWhiteSpace(value))
            return new FieldValidationResult("Skipped", "No value to validate.", "ErpVendorName");

        // Reject own company names — OCR sometimes captures the recipient (us) instead of the sender (vendor).
        // Configured via OwnCompany:Names array in appsettings.json, or OwnCompany:NamesFlat
        // semicolon-delimited string when running in Docker (env var OWN_COMPANY_NAMES).
        static string Norm(string s) =>
            string.Join(" ", s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var normalizedValue = Norm(value);

        var ownNames = _config.GetSection("OwnCompany:Names").Get<string[]>()
            ?? _config["OwnCompany:NamesFlat"]?.Split(';',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            ?? [];

        if (ownNames.Any(n => string.Equals(Norm(n), normalizedValue, StringComparison.OrdinalIgnoreCase)))
            return new FieldValidationResult("Failed",
                $"'{value}' is your own company name, not the vendor. Please correct this field.",
                "ErpVendorName");

        var result = await _erp.LookupVendorByNameAsync(value, ct);
        if (!result.Found)
            return new FieldValidationResult("Failed", $"Vendor '{value}' not found in Acumatica.", "ErpVendorName");
        if (!result.Data!.IsActive)
            return new FieldValidationResult("Warning", $"Vendor '{value}' found (ID: {result.Data.VendorId}) but is inactive.", "ErpVendorName", result.Data);

        // Store resolved VendorId so ErpApInvoiceValidator can cross-check the invoice belongs to this vendor.
        _vendorContext.ResolvedVendorId = result.Data.VendorId;

        return new FieldValidationResult("Passed", $"Vendor verified — ID: {result.Data.VendorId}", "ErpVendorName", result.Data);
    }
}
