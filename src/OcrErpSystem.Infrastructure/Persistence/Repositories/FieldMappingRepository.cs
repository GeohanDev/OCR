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

    public async Task DeleteDocumentTypeAsync(Guid id, CancellationToken ct = default)
    {
        var dt = await _db.DocumentTypes.FindAsync([id], ct);
        if (dt is null) return;

        var now = DateTimeOffset.UtcNow;
        // Soft-delete all field mapping configs
        var configs = await _db.FieldMappingConfigs.Where(c => c.DocumentTypeId == id).ToListAsync(ct);
        foreach (var c in configs)
        {
            c.IsDeleted = true;
            c.DeletedAt = now;
            c.UpdatedAt = now;
        }
        // Soft-delete the document type
        dt.IsDeleted = true;
        dt.DeletedAt = now;
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
            config.IsDeleted = true;
            config.DeletedAt = DateTimeOffset.UtcNow;
            config.UpdatedAt = DateTimeOffset.UtcNow;
            _db.FieldMappingConfigs.Update(config);
            await _db.SaveChangesAsync(ct);
        }
    }

    public async Task<List<FieldMappingConfig>> GetTrashedFieldMappingsAsync(CancellationToken ct = default) =>
        await _db.FieldMappingConfigs.IgnoreQueryFilters()
            .Include(c => c.DocumentType)
            .Where(c => c.IsDeleted)
            .OrderByDescending(c => c.DeletedAt)
            .ToListAsync(ct);

    public async Task<bool> RestoreFieldMappingAsync(Guid id, CancellationToken ct = default)
    {
        var config = await _db.FieldMappingConfigs.IgnoreQueryFilters().FirstOrDefaultAsync(c => c.Id == id, ct);
        if (config is null || !config.IsDeleted) return false;
        config.IsDeleted = false;
        config.DeletedAt = null;
        config.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<List<DocumentType>> GetTrashedDocTypesAsync(CancellationToken ct = default) =>
        await _db.DocumentTypes.IgnoreQueryFilters()
            .Where(dt => dt.IsDeleted)
            .OrderByDescending(dt => dt.DeletedAt)
            .ToListAsync(ct);

    public async Task<bool> RestoreDocTypeAsync(Guid id, CancellationToken ct = default)
    {
        var dt = await _db.DocumentTypes.IgnoreQueryFilters().FirstOrDefaultAsync(d => d.Id == id, ct);
        if (dt is null || !dt.IsDeleted) return false;
        var now = DateTimeOffset.UtcNow;
        dt.IsDeleted = false;
        dt.DeletedAt = null;
        // Also restore all soft-deleted field mapping configs for this type
        var configs = await _db.FieldMappingConfigs.IgnoreQueryFilters()
            .Where(c => c.DocumentTypeId == id && c.IsDeleted)
            .ToListAsync(ct);
        foreach (var c in configs) { c.IsDeleted = false; c.DeletedAt = null; c.UpdatedAt = now; }
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<int> PurgeExpiredAsync(DateTimeOffset cutoff, CancellationToken ct = default)
    {
        var expiredConfigs = await _db.FieldMappingConfigs.IgnoreQueryFilters()
            .Where(c => c.IsDeleted && c.DeletedAt.HasValue && c.DeletedAt.Value < cutoff)
            .ToListAsync(ct);
        _db.FieldMappingConfigs.RemoveRange(expiredConfigs);

        var expiredTypes = await _db.DocumentTypes.IgnoreQueryFilters()
            .Where(dt => dt.IsDeleted && dt.DeletedAt.HasValue && dt.DeletedAt.Value < cutoff)
            .ToListAsync(ct);
        _db.DocumentTypes.RemoveRange(expiredTypes);

        await _db.SaveChangesAsync(ct);
        return expiredConfigs.Count + expiredTypes.Count;
    }
}
