using OcrSystem.Application.Trash;
using OcrSystem.Infrastructure.Persistence.Repositories;

namespace OcrSystem.Infrastructure.Trash;

public class TrashPurgeService : ITrashPurgeService
{
    private readonly DocumentRepository _docRepo;
    private readonly FieldMappingRepository _fieldRepo;

    public TrashPurgeService(DocumentRepository docRepo, FieldMappingRepository fieldRepo)
    {
        _docRepo = docRepo;
        _fieldRepo = fieldRepo;
    }

    public async Task PurgeExpiredAsync(CancellationToken ct = default)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-30);
        await _docRepo.PurgeExpiredAsync(cutoff, ct);
        await _fieldRepo.PurgeExpiredAsync(cutoff, ct);
    }
}
