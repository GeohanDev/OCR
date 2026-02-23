using OcrErpSystem.Application.DTOs;

namespace OcrErpSystem.Application.ERP;

public interface IErpIntegrationService
{
    Task<ErpLookupResult<VendorDto>> LookupVendorAsync(string vendorId, CancellationToken ct = default);
    Task<ErpLookupResult<CurrencyDto>> LookupCurrencyAsync(string currencyCode, CancellationToken ct = default);
    Task<ErpLookupResult<BranchDto>> LookupBranchAsync(string branchCode, CancellationToken ct = default);
    Task<ErpLookupResult<PurchaseOrderDto>> LookupPurchaseOrderAsync(string poNumber, CancellationToken ct = default);
    Task<ErpPushResult> PushDocumentAsync(Guid documentId, CancellationToken ct = default);
    Task<IReadOnlyList<AcumaticaUserDto>> FetchAllUsersAsync(CancellationToken ct = default);
    Task<IReadOnlyList<BranchDto>> FetchAllBranchesAsync(CancellationToken ct = default);
}

public record ErpLookupResult<T>(bool Found, T? Data, string? ErrorMessage);
public record ErpPushResult(bool Success, string? AcumaticaReferenceId, string? ErrorMessage);
