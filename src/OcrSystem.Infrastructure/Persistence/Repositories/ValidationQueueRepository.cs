using Microsoft.EntityFrameworkCore;
using OcrSystem.Domain.Entities;
using OcrSystem.Domain.Enums;

namespace OcrSystem.Infrastructure.Persistence.Repositories;

public class ValidationQueueRepository
{
    private readonly AppDbContext _db;

    public ValidationQueueRepository(AppDbContext db) => _db = db;

    public async Task AddAsync(ValidationQueueItem item, CancellationToken ct = default)
    {
        await _db.ValidationQueue.AddAsync(item, ct);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<ValidationQueueItem?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        await _db.ValidationQueue.FirstOrDefaultAsync(q => q.Id == id, ct);

    public async Task UpdateAsync(ValidationQueueItem item, CancellationToken ct = default)
    {
        _db.ValidationQueue.Update(item);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<List<ValidationQueueItem>> GetRecentAsync(int count = 20, CancellationToken ct = default) =>
        await _db.ValidationQueue
            .OrderByDescending(q => q.CreatedAt)
            .Take(count)
            .ToListAsync(ct);

    public async Task CancelPendingAsync(Guid documentId, CancellationToken ct = default)
    {
        var items = await _db.ValidationQueue
            .Where(q => q.DocumentId == documentId &&
                        (q.Status == ValidationQueueStatus.Pending ||
                         q.Status == ValidationQueueStatus.Processing))
            .ToListAsync(ct);
        if (items.Count == 0) return;
        foreach (var item in items)
        {
            item.Status = ValidationQueueStatus.Cancelled;
            item.CompletedAt = DateTimeOffset.UtcNow;
        }
        await _db.SaveChangesAsync(ct);
    }
}
