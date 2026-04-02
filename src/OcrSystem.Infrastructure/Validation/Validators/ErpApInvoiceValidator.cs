using OcrSystem.Application.DTOs;
using OcrSystem.Application.ERP;
using OcrSystem.Application.Validation;

namespace OcrSystem.Infrastructure.Validation.Validators;

public class ErpApInvoiceValidator : IFieldValidator
{
    private readonly IErpIntegrationService _erp;
    private readonly IVendorResolutionContext _vendorContext;
    private readonly IValidationFieldContext _fieldContext;
    private readonly IOwnCompanyService _ownCompany;
    private readonly IErpInvoiceContext _invoiceContext;

    public ErpApInvoiceValidator(
        IErpIntegrationService erp,
        IVendorResolutionContext vendorContext,
        IValidationFieldContext fieldContext,
        IOwnCompanyService ownCompany,
        IErpInvoiceContext invoiceContext)
    {
        _erp = erp;
        _vendorContext = vendorContext;
        _fieldContext = fieldContext;
        _ownCompany = ownCompany;
        _invoiceContext = invoiceContext;
    }

    public IReadOnlyList<string> SupportedErpMappingKeys => ["ApInvoiceNbr"];
    public bool RunForAllFields => false;

    public async Task<FieldValidationResult> ValidateAsync(ExtractedFieldDto field, FieldMappingConfigDto config, CancellationToken ct = default)
    {
        var value = (field.CorrectedValue ?? field.NormalizedValue ?? field.RawValue)?.Trim();
        if (string.IsNullOrWhiteSpace(value))
            return new FieldValidationResult("Skipped", "No value to validate.", "ErpApInvoice");

        string? vendorName = null;
        if (!string.IsNullOrWhiteSpace(config.DependentFieldKey))
            _fieldContext.FieldValues.TryGetValue(config.DependentFieldKey, out vendorName);

        if (!string.IsNullOrWhiteSpace(vendorName) && _ownCompany.IsOwnCompanyName(vendorName))
            return new FieldValidationResult("Warning",
                $"Vendor name '{vendorName}' is your own company name — please verify this document is from an external vendor.",
                "ErpApInvoice");

        // Priority 1: vendor ID already resolved (from header validation or a previous field in
        // this row) — use the exact vendor ID filter for the most precise lookup.
        if (!string.IsNullOrWhiteSpace(_vendorContext.ResolvedVendorId))
        {
            var filteredResult = await _erp.LookupBillByVendorRefAndVendorIdAsync(
                value, _vendorContext.ResolvedVendorId, ct);

            if (filteredResult.Found && filteredResult.Data is not null)
            {
                if (_invoiceContext.IsRowValidation)
                    _invoiceContext.SetCachedInvoice(value, filteredResult.Data);

                filteredResult.Data.TryGetValue("ReferenceNbr", out var refNbr);
                filteredResult.Data.TryGetValue("Type", out var docType);
                filteredResult.Data.TryGetValue("Vendor", out var vendorId);
                filteredResult.Data.TryGetValue("Date", out var docDate);
                filteredResult.Data.TryGetValue("Status", out var status);
                return new FieldValidationResult("Passed",
                    $"Invoice verified — {docType} {refNbr ?? value}, Vendor: {vendorId}, Date: {docDate}, Status: {status}",
                    "ErpApInvoice", filteredResult.Data);
            }

            return new FieldValidationResult("Warning",
                $"Invoice '{value}' not found for vendor ID '{_vendorContext.ResolvedVendorId}' in Acumatica.",
                "ErpApInvoice");
        }

        // Priority 2: no vendor ID yet but vendor name is available — look up by name and
        // resolve the vendor ID from the result for subsequent validators in this row.
        if (!string.IsNullOrWhiteSpace(vendorName))
        {
            if (_vendorContext.VendorValidationFailed)
                return new FieldValidationResult("Warning",
                    $"Invoice '{value}' cannot be verified — vendor validation failed.",
                    "ErpApInvoice");

            var result = await _erp.LookupApInvoiceByVendorAsync(value, vendorName, ct);
            if (!result.Found)
                return new FieldValidationResult("Warning",
                    $"Invoice '{value}' not found for vendor '{vendorName}' in Acumatica.",
                    "ErpApInvoice");

            var inv = result.Data!;
            if (!string.IsNullOrWhiteSpace(inv.VendorId))
                _vendorContext.ResolvedVendorId = inv.VendorId;

            // In row validation, do a richer lookup and cache for date/amount validators.
            if (_invoiceContext.IsRowValidation && !string.IsNullOrWhiteSpace(inv.VendorId))
            {
                var rich = await _erp.LookupBillByVendorRefAndVendorIdAsync(value, inv.VendorId, ct);
                if (rich.Found && rich.Data is not null)
                    _invoiceContext.SetCachedInvoice(value, rich.Data);
            }

            return new FieldValidationResult("Passed",
                $"Invoice verified — {inv.DocType} {inv.RefNbr}, Vendor: {inv.VendorId}, Date: {inv.DocDate}, Status: {inv.Status}",
                "ErpApInvoice", inv);
        }

        // Priority 3: neither vendor ID nor vendor name — warn and fall back to a
        // ref-number-only lookup (least precise, may match invoices from other vendors).
        if (_vendorContext.VendorValidationFailed)
            return new FieldValidationResult("Warning",
                $"Invoice '{value}' cannot be verified — vendor validation failed.",
                "ErpApInvoice");

        var fallback = await _erp.LookupApInvoiceAsync(value, ct);
        if (!fallback.Found)
            return new FieldValidationResult("Warning",
                $"Invoice '{value}' not found in Acumatica AP.",
                "ErpApInvoice");

        var fallbackInv = fallback.Data!;
        // Do NOT set ResolvedVendorId here — this lookup is by Acumatica's internal ReferenceNbr
        // (not VendorRef), so any matched vendor may not be the actual document vendor.
        // Setting it would cause date/amount cross-field validators to fetch the wrong vendor's bill.

        return new FieldValidationResult("Passed",
            $"Invoice verified — {fallbackInv.DocType} {fallbackInv.RefNbr}, Vendor: {fallbackInv.VendorId}, Date: {fallbackInv.DocDate}, Status: {fallbackInv.Status}",
            "ErpApInvoice", fallbackInv);
    }
}
