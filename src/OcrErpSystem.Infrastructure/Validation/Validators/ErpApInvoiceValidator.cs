using OcrErpSystem.Application.DTOs;
using OcrErpSystem.Application.ERP;
using OcrErpSystem.Application.Validation;

namespace OcrErpSystem.Infrastructure.Validation.Validators;

public class ErpApInvoiceValidator : IFieldValidator
{
    private readonly IErpIntegrationService _erp;
    private readonly IVendorResolutionContext _vendorContext;

    public ErpApInvoiceValidator(IErpIntegrationService erp, IVendorResolutionContext vendorContext)
    {
        _erp = erp;
        _vendorContext = vendorContext;
    }

    public IReadOnlyList<string> SupportedErpMappingKeys => ["ApInvoiceNbr"];
    public bool RunForAllFields => false;

    public async Task<FieldValidationResult> ValidateAsync(ExtractedFieldDto field, FieldMappingConfigDto config, CancellationToken ct = default)
    {
        var value = (field.CorrectedValue ?? field.NormalizedValue ?? field.RawValue)?.Trim();
        if (string.IsNullOrWhiteSpace(value))
            return new FieldValidationResult("Skipped", "No value to validate.", "ErpApInvoice");

        var result = await _erp.LookupApInvoiceAsync(value, ct);
        if (!result.Found)
            return new FieldValidationResult("Warning", $"Invoice '{value}' not found in Acumatica AP.", "ErpApInvoice");

        var inv = result.Data!;

        // Cross-check: the invoice must belong to the vendor already resolved from the VendorName field.
        // This prevents a case where the correct invoice number is entered but for the wrong vendor.
        if (!string.IsNullOrEmpty(_vendorContext.ResolvedVendorId) &&
            !string.IsNullOrEmpty(inv.VendorId) &&
            !string.Equals(inv.VendorId, _vendorContext.ResolvedVendorId, StringComparison.OrdinalIgnoreCase))
        {
            return new FieldValidationResult("Failed",
                $"Invoice '{value}' found but belongs to Vendor '{inv.VendorId}', not '{_vendorContext.ResolvedVendorId}'. Please verify the invoice number.",
                "ErpApInvoice", inv);
        }

        return new FieldValidationResult("Passed",
            $"Invoice verified — {inv.DocType} {inv.RefNbr}, Vendor: {inv.VendorId}, Date: {inv.DocDate}, Status: {inv.Status}",
            "ErpApInvoice", inv);
    }
}
