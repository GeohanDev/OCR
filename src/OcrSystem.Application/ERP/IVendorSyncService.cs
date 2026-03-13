namespace OcrSystem.Application.ERP;

public interface IVendorSyncService
{
    /// <summary>
    /// Fetches all vendors from Acumatica and upserts them into the local vendor table.
    /// Returns the number of vendors synced.
    /// </summary>
    Task<int> SyncVendorsAsync(CancellationToken ct = default);
}
