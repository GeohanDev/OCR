namespace OcrErpSystem.Application.Trash;

public interface ITrashPurgeService
{
    Task PurgeExpiredAsync(CancellationToken ct = default);
}
