using OcrSystem.Application.DTOs;

namespace OcrSystem.Application.ERP;

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
    /// Fetches ALL open AP bills across all vendors in one call.
    /// Each bill includes VendorId, Balance, and DueDate for aging computation.
    /// Used by AgingSnapshotService to compute Kind 1 (current aging) for all vendors at once.
    /// </summary>
    Task<IReadOnlyList<OpenBillDto>> FetchAllOpenBillsForAgingAsync(CancellationToken ct = default);

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
    /// Calls the APAging Generic Inquiry (GI) in Acumatica.
    /// Parameters: BranchID (optional), VendorID (optional), AgeDate (required).
    /// Returns one row per vendor as a flat string dictionary of GI columns.
    /// </summary>
    Task<IReadOnlyList<IReadOnlyDictionary<string, string>>> FetchApAgingGiAsync(
        string? branchId, string? vendorId, DateTimeOffset ageDate, CancellationToken ct = default);

    /// <summary>
    /// Fetches ALL vendor rows from the APAging GI in one call:
    /// PUT to set the AgeDate parameter, then GET the full result set.
    /// Returns one row per vendor with aging buckets + VendorID.
    /// </summary>
    Task<IReadOnlyList<IReadOnlyDictionary<string, string>>> FetchAllApAgingRowsAsync(
        DateTimeOffset ageDate, CancellationToken ct = default);

    /// <summary>
    /// Fetches all vendor aging rows from the AllAPAging GI for a specific branch.
    /// PUT to set Branch + AgeasofDate, returns every vendor row for that branch.
    /// Call once per active branch and aggregate to get full Kind 1 (current aging) coverage.
    /// </summary>
    Task<IReadOnlyList<IReadOnlyDictionary<string, string>>> FetchAllApAgingFromAllGiAsync(
        string branchId, DateTimeOffset ageDate, CancellationToken ct = default);

    /// <summary>
    /// Fires a raw authenticated GET to any URL and returns (statusCode, first 1000 chars of body).
    /// Used by diagnostic probes to test URL patterns without going through parsing logic.
    /// </summary>
    Task<(int Status, string Body)> RawGetAsync(string url, CancellationToken ct = default);

    /// <summary>
    /// Lightweight call to verify the current token is still valid.
    /// Returns true on success; throws AcumaticaAuthException on 401/403.
    /// </summary>
    Task<bool> PingAsync(CancellationToken ct = default);

    /// <summary>
    /// Fetches the first record from any Acumatica entity (no filter, $top=1) to verify
    /// the endpoint exists, auth works, and reveal the actual OData field names.
    /// </summary>
    Task<ErpLookupResult<IReadOnlyDictionary<string, string>>> ProbeEntityAsync(
        string entity, CancellationToken ct = default);
}

public record ErpLookupResult<T>(bool Found, T? Data, string? ErrorMessage);
public record ErpPushResult(bool Success, string? AcumaticaReferenceId, string? ErrorMessage);
