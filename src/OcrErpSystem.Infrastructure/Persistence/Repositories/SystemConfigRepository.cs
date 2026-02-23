using Microsoft.EntityFrameworkCore;
using OcrErpSystem.Domain.Entities;

namespace OcrErpSystem.Infrastructure.Persistence.Repositories;

public class SystemConfigRepository
{
    private readonly AppDbContext _db;

    public SystemConfigRepository(AppDbContext db) => _db = db;

    public async Task<string?> GetValueAsync(string key, CancellationToken ct = default)
    {
        var config = await _db.SystemConfigs.FindAsync([key], ct);
        return config?.Value;
    }

    public async Task<Dictionary<string, string>> GetAllAsync(bool includeSensitive, CancellationToken ct = default)
    {
        var q = _db.SystemConfigs.AsQueryable();
        if (!includeSensitive) q = q.Where(c => !c.IsSensitive);
        return await q.ToDictionaryAsync(c => c.Key, c => c.Value, ct);
    }

    public async Task UpsertAsync(SystemConfig config, CancellationToken ct = default)
    {
        var existing = await _db.SystemConfigs.FindAsync([config.Key], ct);
        if (existing is null)
            await _db.SystemConfigs.AddAsync(config, ct);
        else
        {
            existing.Value = config.Value;
            existing.Description = config.Description;
            existing.IsSensitive = config.IsSensitive;
            existing.UpdatedBy = config.UpdatedBy;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
        }
        await _db.SaveChangesAsync(ct);
    }
}
