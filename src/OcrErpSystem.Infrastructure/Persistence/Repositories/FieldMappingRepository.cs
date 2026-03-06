using Microsoft.EntityFrameworkCore;
using OcrErpSystem.Domain.Entities;

namespace OcrErpSystem.Infrastructure.Persistence.Repositories;

public class FieldMappingRepository
{
    private readonly AppDbContext _db;

    public FieldMappingRepository(AppDbContext db) => _db = db;

    public async Task<List<DocumentType>> GetDocumentTypesAsync(CancellationToken ct = default) =>
        await _db.DocumentTypes.OrderBy(dt => dt.DisplayName).ToListAsync(ct);

    public async Task<DocumentType?> GetDocumentTypeByIdAsync(Guid id, CancellationToken ct = default) =>
        await _db.DocumentTypes.FindAsync([id], ct);

    public async Task AddDocumentTypeAsync(DocumentType dt, CancellationToken ct = default)
    {
        await _db.DocumentTypes.AddAsync(dt, ct);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<List<FieldMappingConfig>> GetFieldMappingsAsync(Guid documentTypeId, bool activeOnly = false, CancellationToken ct = default)
    {
        var q = _db.FieldMappingConfigs.Where(fmc => fmc.DocumentTypeId == documentTypeId);
        if (activeOnly) q = q.Where(fmc => fmc.IsActive);
        return await q.OrderBy(fmc => fmc.DisplayOrder).ThenBy(fmc => fmc.FieldName).ToListAsync(ct);
    }

    public async Task<FieldMappingConfig?> GetFieldMappingByIdAsync(Guid id, CancellationToken ct = default) =>
        await _db.FieldMappingConfigs.FindAsync([id], ct);

    public async Task AddFieldMappingAsync(FieldMappingConfig config, CancellationToken ct = default)
    {
        await _db.FieldMappingConfigs.AddAsync(config, ct);
        await _db.SaveChangesAsync(ct);
    }

    public async Task UpdateFieldMappingAsync(FieldMappingConfig config, CancellationToken ct = default)
    {
        _db.FieldMappingConfigs.Update(config);
        await _db.SaveChangesAsync(ct);
    }

    public async Task DeleteFieldMappingAsync(Guid id, CancellationToken ct = default)
    {
        var config = await _db.FieldMappingConfigs.FindAsync([id], ct);
        if (config is not null)
        {
            config.IsActive = false;
            config.UpdatedAt = DateTimeOffset.UtcNow;
            _db.FieldMappingConfigs.Update(config);
            await _db.SaveChangesAsync(ct);
        }
    }
}
