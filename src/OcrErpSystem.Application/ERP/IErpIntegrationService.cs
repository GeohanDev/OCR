using OcrErpSystem.Application.DTOs;

namespace OcrErpSystem.Application.ERP;

public interface IErpIntegrationService
{
    Task<ErpLookupResult<VendorDto>> LookupVendorAsync(string vendorId, CancellationToken ct = default);
    Task<ErpLookupResult<VendorDto>> LookupVendorByNameAsync(string vendorName, CancellationToken ct = default);
    Task<ErpLookupResult<ApInvoiceDto>> LookupApInvoiceAsync(string invoiceNbr, CancellationToken ct = default);
    Task<ErpLookupResult<CurrencyDto>> LookupCurrencyAsync(string currencyCode, CancellationToken ct = default);
    Task<ErpLookupResult<BranchDto>> LookupBranchAsync(string branchCode, CancellationToken ct = default);
    Task<ErpLookupResult<PurchaseOrderDto>> LookupPurchaseOrderAsync(string poNumber, CancellationToken ct = default);
    Task<ErpPushResult> PushDocumentAsync(Guid documentId, CancellationToken ct = default);
    Task<IReadOnlyList<AcumaticaUserDto>> FetchAllUsersAsync(CancellationToken ct = default);
    Task<IReadOnlyList<BranchDto>> FetchAllBranchesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<VendorDto>> GetAllVendorsAsync(int? top = null, CancellationToken ct = default);

    /// <summary>
    /// Generic OData lookup: queries any Acumatica entity and checks if a record
    /// exists where <paramref name="field"/> equals <paramref name="value"/>.
    /// Returns the first matching record's fields as a flat string dictionary.
    /// </summary>
    Task<ErpLookupResult<IReadOnlyDictionary<string, string>>> LookupGenericAsync(
        string entity, string field, string value, CancellationToken ct = default);

    /// <summary>Known Acumatica entities and the fields available for ERP mapping.</summary>
    IReadOnlyList<ErpEntityDto> GetEntityCatalog();

    /// <summary>
    /// Fetches the first record from any Acumatica entity (no filter, $top=1) to verify
    /// the endpoint exists, auth works, and reveal the actual OData field names.
    /// </summary>
    Task<ErpLookupResult<IReadOnlyDictionary<string, string>>> ProbeEntityAsync(
        string entity, CancellationToken ct = default);
}

public record ErpLookupResult<T>(bool Found, T? Data, string? ErrorMessage);
public record ErpPushResult(bool Success, string? AcumaticaReferenceId, string? ErrorMessage);
