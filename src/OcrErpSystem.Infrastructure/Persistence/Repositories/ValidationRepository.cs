using Microsoft.EntityFrameworkCore;
using OcrErpSystem.Domain.Enums;

namespace OcrErpSystem.Infrastructure.Persistence.Repositories;

public class ValidationRepository
{
    private readonly AppDbContext _db;

    public ValidationRepository(AppDbContext db) => _db = db;

    public async Task<List<Domain.Entities.ValidationResult>> GetByDocumentIdAsync(Guid documentId, CancellationToken ct = default) =>
        await _db.ValidationResults
            .Where(v => v.DocumentId == documentId)
            .OrderBy(v => v.ValidatedAt)
            .ToListAsync(ct);

    public async Task DeleteByDocumentIdAsync(Guid documentId, CancellationToken ct = default)
    {
        var existing = await _db.ValidationResults.Where(v => v.DocumentId == documentId).ToListAsync(ct);
        _db.ValidationResults.RemoveRange(existing);
        await _db.SaveChangesAsync(ct);
    }

    public async Task AddRangeAsync(IEnumerable<Domain.Entities.ValidationResult> results, CancellationToken ct = default)
    {
        await _db.ValidationResults.AddRangeAsync(results, ct);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<bool> HasBlockingFailuresAsync(Guid documentId, CancellationToken ct = default)
    {
        return await (
            from vr in _db.ValidationResults
            join ef in _db.ExtractedFields on vr.ExtractedFieldId equals ef.Id into efj
            from ef in efj.DefaultIfEmpty()
            join fmc in _db.FieldMappingConfigs on ef.FieldMappingConfigId equals fmc.Id into fmcj
            from fmc in fmcj.DefaultIfEmpty()
            where vr.DocumentId == documentId
                  && vr.Status == ValidationStatus.Failed
                  && fmc != null && fmc.IsRequired
            select vr.Id
        ).AnyAsync(ct);
    }
}
