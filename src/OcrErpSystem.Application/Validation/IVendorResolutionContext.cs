namespace OcrErpSystem.Application.Validation;

/// <summary>
/// Scoped context that carries the resolved Acumatica VendorId across field validators
/// within a single validation run. ErpVendorNameValidator writes to it;
/// ErpApInvoiceValidator reads from it to cross-check the invoice belongs to the same vendor.
/// </summary>
public interface IVendorResolutionContext
{
    /// <summary>The Acumatica VendorID resolved from the VendorName field, or null if not yet resolved.</summary>
    string? ResolvedVendorId { get; set; }
}
