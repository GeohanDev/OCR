using OcrSystem.Application.Validation;

namespace OcrSystem.Infrastructure.Validation;

public class ErpInvoiceContext : IErpInvoiceContext
{
    public bool IsRowValidation { get; set; }
    public IReadOnlyDictionary<string, string>? CachedInvoiceData { get; private set; }
    public string? CachedVendorRef { get; private set; }

    public void SetCachedInvoice(string vendorRef, IReadOnlyDictionary<string, string> data)
    {
        CachedVendorRef = vendorRef;
        CachedInvoiceData = data;
    }

    public void ClearCache()
    {
        CachedVendorRef = null;
        CachedInvoiceData = null;
    }
}
