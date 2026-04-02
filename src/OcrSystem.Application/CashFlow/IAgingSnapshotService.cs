namespace OcrSystem.Application.CashFlow;

public interface IAgingSnapshotService
{
    /// <summary>
    /// Captures an ERP AP aging snapshot for all vendors for the current date.
    /// Skips if a snapshot already exists for today unless <paramref name="force"/> is true.
    /// </summary>
    Task<int> CaptureSnapshotAsync(bool force = false, string? acumaticaToken = null, CancellationToken ct = default);
}
