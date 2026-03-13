namespace OcrSystem.Application.Validation;

/// <summary>
/// Scoped context that carries the resolved Acumatica VendorId across field validators
/// within a single validation run. ErpVendorNameValidator writes to it;
/// ErpApInvoiceValidator reads from it to cross-check the invoice belongs to the same vendor.
/// </summary>
public interface IVendorResolutionContext
{
    /// <summary>The Acumatica VendorID resolved from the VendorName field, or null if not yet resolved.</summary>
    string? ResolvedVendorId { get; set; }

    /// <summary>
    /// Set to true when the vendor name field validation failed (own-company name, not found, etc.).
    /// When true, invoice validators must not pass — the vendor is unverified so ownership cannot be confirmed.
    /// </summary>
    bool VendorValidationFailed { get; set; }
}
