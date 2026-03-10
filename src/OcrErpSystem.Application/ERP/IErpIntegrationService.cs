using OcrErpSystem.Application.DTOs;

namespace OcrErpSystem.Application.ERP;

public interface IErpIntegrationService
{
    Task<ErpLookupResult<VendorDto>> LookupVendorAsync(string vendorId, CancellationToken ct = default);
    Task<ErpLookupResult<VendorDto>> LookupVendorByNameAsync(string vendorName, CancellationToken ct = default);
    Task<ErpLookupResult<ApInvoiceDto>> LookupApInvoiceAsync(string invoiceNbr, CancellationToken ct = default);

    /// <summary>
    /// Looks up an AP invoice filtered by both VendorRef and VendorName in a single REST call.
    /// Returns not-found when either the invoice doesn't exist or doesn't belong to that vendor.
    /// </summary>
    Task<ErpLookupResult<ApInvoiceDto>> LookupApInvoiceByVendorAsync(string vendorRef, string vendorName, CancellationToken ct = default);

    /// <summary>
    /// Looks up a Bill record by VendorRef AND Vendor (VendorID), returning the full field dictionary.
    /// Used by cross-field validators (e.g. Bill:Amount) to ensure the correct bill is returned
    /// when multiple bills share the same VendorRef across different vendors.
    /// </summary>
    Task<ErpLookupResult<IReadOnlyDictionary<string, string>>> LookupBillByVendorRefAndVendorIdAsync(
        string vendorRef, string vendorId, CancellationToken ct = default);
    Task<ErpLookupResult<CurrencyDto>> LookupCurrencyAsync(string currencyCode, CancellationToken ct = default);
    Task<ErpLookupResult<BranchDto>> LookupBranchAsync(string branchCode, CancellationToken ct = default);

    /// <summary>
    /// Looks up the AP ending balance for a vendor in a specific financial period.
    /// Queries the APHistory entity filtered by VendorID and FinPeriodID.
    /// Period format: YYYYMM (e.g. "202501" for January 2025).
    /// </summary>
    Task<ErpLookupResult<IReadOnlyDictionary<string, string>>> LookupVendorBalanceAsync(
        string vendorId, string period, CancellationToken ct = default);
    Task<ErpLookupResult<PurchaseOrderDto>> LookupPurchaseOrderAsync(string poNumber, CancellationToken ct = default);
    Task<ErpPushResult> PushDocumentAsync(Guid documentId, CancellationToken ct = default);
    Task<IReadOnlyList<AcumaticaUserDto>> FetchAllUsersAsync(CancellationToken ct = default);
    Task<IReadOnlyList<BranchDto>> FetchAllBranchesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<VendorDto>> GetAllVendorsAsync(int? top = null, CancellationToken ct = default);

    /// <summary>
    /// Fetches all vendors from Acumatica with extended details (address, payment terms).
    /// Used by the vendor sync service to populate the local vendor table.
    /// </summary>
    Task<IReadOnlyList<VendorFullDto>> GetAllVendorsFullAsync(CancellationToken ct = default);

    /// <summary>
    /// Fetches all open AP bills for a given vendor (Balance > 0, Status ne Closed/Voided).
    /// Used by the vendor statement validator for outstanding balance and aging checks.
    /// </summary>
    Task<IReadOnlyList<OpenBillDto>> FetchOpenBillsForVendorAsync(string vendorId, CancellationToken ct = default);

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
    /// Queries the Acumatica OData service document to return all available entity names
    /// for the configured API version. Useful for discovering entity names that differ by version.
    /// </summary>
    Task<IReadOnlyList<string>> GetAvailableODataEntitiesAsync(CancellationToken ct = default);

    /// <summary>Returns the raw HTTP response from the OData service document endpoint for debugging.</summary>
    Task<string> GetODataServiceDocumentRawAsync(CancellationToken ct = default);

    /// <summary>
    /// Fetches the first record from any Acumatica entity (no filter, $top=1) to verify
    /// the endpoint exists, auth works, and reveal the actual OData field names.
    /// </summary>
    Task<ErpLookupResult<IReadOnlyDictionary<string, string>>> ProbeEntityAsync(
        string entity, CancellationToken ct = default);
}

public record ErpLookupResult<T>(bool Found, T? Data, string? ErrorMessage);
public record ErpPushResult(bool Success, string? AcumaticaReferenceId, string? ErrorMessage);
