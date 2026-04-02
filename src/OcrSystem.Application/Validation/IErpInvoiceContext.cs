namespace OcrSystem.Application.Validation;

/// <summary>
/// Scoped cache that stores the invoice record fetched by ErpApInvoiceValidator so that
/// other validators in the same row (date, amount) can reuse it without a second ERP call.
/// Set IsRowValidation = true in ValidateRowAsync to activate caching.
/// </summary>
public interface IErpInvoiceContext
{
    bool IsRowValidation { get; set; }
    IReadOnlyDictionary<string, string>? CachedInvoiceData { get; }
    string? CachedVendorRef { get; }
    void SetCachedInvoice(string vendorRef, IReadOnlyDictionary<string, string> data);
    void ClearCache();
}
