using Microsoft.EntityFrameworkCore;
using OcrSystem.Domain.Entities;

namespace OcrSystem.Infrastructure.Persistence.Repositories;

public class OcrResultRepository
{
    private readonly AppDbContext _db;

    public OcrResultRepository(AppDbContext db) => _db = db;

    public async Task<OcrResult?> GetByDocumentIdAsync(Guid documentId, CancellationToken ct = default) =>
        await _db.OcrResults
            .Include(r => r.ExtractedFields.OrderBy(f => f.SortOrder))
                .ThenInclude(f => f.FieldMappingConfig)
            .OrderByDescending(r => r.CreatedAt)
            .FirstOrDefaultAsync(r => r.DocumentId == documentId, ct);

    public async Task<OcrResult?> GetByIdAsync(Guid ocrResultId, CancellationToken ct = default) =>
        await _db.OcrResults.FirstOrDefaultAsync(r => r.Id == ocrResultId, ct);

    public async Task<ExtractedField?> GetFieldByIdAsync(Guid fieldId, CancellationToken ct = default) =>
        await _db.ExtractedFields
            .Include(f => f.FieldMappingConfig)
            .FirstOrDefaultAsync(f => f.Id == fieldId, ct);

    public async Task AddResultAsync(OcrResult result, CancellationToken ct = default)
    {
        await _db.OcrResults.AddAsync(result, ct);
        await _db.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(OcrResult result, CancellationToken ct = default)
    {
        _db.OcrResults.Update(result);
        await _db.SaveChangesAsync(ct);
    }

    public async Task UpdateFieldAsync(ExtractedField field, CancellationToken ct = default)
    {
        _db.ExtractedFields.Update(field);
        await _db.SaveChangesAsync(ct);
    }

    public async Task DeleteFieldAsync(Guid fieldId, CancellationToken ct = default)
    {
        var field = await _db.ExtractedFields.FindAsync([fieldId], ct);
        if (field is null) throw new KeyNotFoundException($"ExtractedField {fieldId} not found");
        _db.ExtractedFields.Remove(field);
        await _db.SaveChangesAsync(ct);
    }
}
