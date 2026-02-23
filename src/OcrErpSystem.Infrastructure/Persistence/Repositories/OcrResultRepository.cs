using Microsoft.EntityFrameworkCore;
using OcrErpSystem.Domain.Entities;

namespace OcrErpSystem.Infrastructure.Persistence.Repositories;

public class OcrResultRepository
{
    private readonly AppDbContext _db;

    public OcrResultRepository(AppDbContext db) => _db = db;

    public async Task<OcrResult?> GetByDocumentIdAsync(Guid documentId, CancellationToken ct = default) =>
        await _db.OcrResults
            .Include(r => r.ExtractedFields)
                .ThenInclude(f => f.FieldMappingConfig)
            .OrderByDescending(r => r.CreatedAt)
            .FirstOrDefaultAsync(r => r.DocumentId == documentId, ct);

    public async Task<ExtractedField?> GetFieldByIdAsync(Guid fieldId, CancellationToken ct = default) =>
        await _db.ExtractedFields
            .Include(f => f.FieldMappingConfig)
            .FirstOrDefaultAsync(f => f.Id == fieldId, ct);

    public async Task AddResultAsync(OcrResult result, CancellationToken ct = default)
    {
        await _db.OcrResults.AddAsync(result, ct);
        await _db.SaveChangesAsync(ct);
    }

    public async Task UpdateFieldAsync(ExtractedField field, CancellationToken ct = default)
    {
        _db.ExtractedFields.Update(field);
        await _db.SaveChangesAsync(ct);
    }
}
