using Microsoft.EntityFrameworkCore;
using OcrSystem.Application.Audit;
using OcrSystem.Common;
using OcrSystem.Domain.Entities;

namespace OcrSystem.Infrastructure.Persistence.Repositories;

public class AuditRepository
{
    private readonly AppDbContext _db;

    public AuditRepository(AppDbContext db) => _db = db;

    public async Task AddAsync(AuditLog log, CancellationToken ct = default)
    {
        await _db.AuditLogs.AddAsync(log, ct);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<PagedResult<AuditLog>> QueryAsync(AuditLogQuery query, CancellationToken ct = default)
    {
        var q = _db.AuditLogs.Include(a => a.ActorUser).AsQueryable();

        if (query.DocumentId.HasValue)
            q = q.Where(a => a.DocumentId == query.DocumentId);
        if (query.ActorUserId.HasValue)
            q = q.Where(a => a.ActorUserId == query.ActorUserId);
        if (!string.IsNullOrWhiteSpace(query.EventType))
            q = q.Where(a => a.EventType == query.EventType);
        if (query.From.HasValue)
            q = q.Where(a => a.OccurredAt >= query.From);
        if (query.To.HasValue)
            q = q.Where(a => a.OccurredAt <= query.To);

        var total = await q.CountAsync(ct);
        var items = await q.OrderByDescending(a => a.OccurredAt)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .ToListAsync(ct);

        return new PagedResult<AuditLog> { Items = items, TotalCount = total, Page = query.Page, PageSize = query.PageSize };
    }
}
