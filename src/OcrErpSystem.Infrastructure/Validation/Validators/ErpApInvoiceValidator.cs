using OcrErpSystem.Application.DTOs;
using OcrErpSystem.Application.ERP;
using OcrErpSystem.Application.Validation;

namespace OcrErpSystem.Infrastructure.Validation.Validators;

public class ErpApInvoiceValidator : IFieldValidator
{
    private readonly IErpIntegrationService _erp;
    private readonly IVendorResolutionContext _vendorContext;
    private readonly IValidationFieldContext _fieldContext;
    private readonly IOwnCompanyService _ownCompany;

    public ErpApInvoiceValidator(
        IErpIntegrationService erp,
        IVendorResolutionContext vendorContext,
        IValidationFieldContext fieldContext,
        IOwnCompanyService ownCompany)
    {
        _erp = erp;
        _vendorContext = vendorContext;
        _fieldContext = fieldContext;
        _ownCompany = ownCompany;
    }

    public IReadOnlyList<string> SupportedErpMappingKeys => ["ApInvoiceNbr"];
    public bool RunForAllFields => false;

    public async Task<FieldValidationResult> ValidateAsync(ExtractedFieldDto field, FieldMappingConfigDto config, CancellationToken ct = default)
    {
        var value = (field.CorrectedValue ?? field.NormalizedValue ?? field.RawValue)?.Trim();
        if (string.IsNullOrWhiteSpace(value))
            return new FieldValidationResult("Skipped", "No value to validate.", "ErpApInvoice");

        // Resolve vendor name: prefer DependentFieldKey (explicit cross-validation config),
        // then fall back to IVendorResolutionContext populated by ErpVendorNameValidator.
        string? vendorName = null;
        if (!string.IsNullOrWhiteSpace(config.DependentFieldKey))
            _fieldContext.FieldValues.TryGetValue(config.DependentFieldKey, out vendorName);

        if (!string.IsNullOrWhiteSpace(vendorName))
        {
            // Own-company guard: invoice cannot belong to our own company.
            if (_ownCompany.IsOwnCompanyName(vendorName))
                return new FieldValidationResult("Warning",
                    $"Vendor name '{vendorName}' is your own company name — please verify this document is from an external vendor.",
                    "ErpApInvoice");

            // Single REST call filtered by both VendorRef and VendorName.
            // If Acumatica returns 0 results, the invoice doesn't exist for this vendor.
            var result = await _erp.LookupApInvoiceByVendorAsync(value, vendorName, ct);
            if (!result.Found)
                return new FieldValidationResult("Warning",
                    $"Invoice '{value}' not found for vendor '{vendorName}' in Acumatica.",
                    "ErpApInvoice");

            var inv = result.Data!;
            // Store resolved VendorId so DynamicErpValidator can use it for cross-field
            // Amount/Date lookups filtered by the correct vendor.
            if (!string.IsNullOrWhiteSpace(inv.VendorId))
                _vendorContext.ResolvedVendorId = inv.VendorId;

            return new FieldValidationResult("Passed",
                $"Invoice verified — {inv.DocType} {inv.RefNbr}, Vendor: {inv.VendorId}, Date: {inv.DocDate}, Status: {inv.Status}",
                "ErpApInvoice", inv);
        }
        else
        {
            // No vendor name available — check vendor context before falling back to invoice-only lookup.
            if (_vendorContext.VendorValidationFailed)
                return new FieldValidationResult("Warning",
                    $"Invoice '{value}' cannot be verified — vendor validation failed.",
                    "ErpApInvoice");

            var result = await _erp.LookupApInvoiceAsync(value, ct);
            if (!result.Found)
                return new FieldValidationResult("Warning",
                    $"Invoice '{value}' not found in Acumatica AP.",
                    "ErpApInvoice");

            var inv = result.Data!;
            return new FieldValidationResult("Passed",
                $"Invoice verified — {inv.DocType} {inv.RefNbr}, Vendor: {inv.VendorId}, Date: {inv.DocDate}, Status: {inv.Status}",
                "ErpApInvoice", inv);
        }
    }

}
