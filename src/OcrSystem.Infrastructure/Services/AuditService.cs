using System.Text.Json;
using OcrSystem.Application.Audit;
using OcrSystem.Application.DTOs;
using OcrSystem.Common;
using OcrSystem.Domain.Entities;
using OcrSystem.Infrastructure.Persistence.Repositories;

namespace OcrSystem.Infrastructure.Services;

public class AuditService : IAuditService
{
    private readonly AuditRepository _repo;

    public AuditService(AuditRepository repo) => _repo = repo;

    public async Task LogAsync(string eventType, Guid? actorUserId, Guid? documentId,
        string? targetEntityType = null, string? targetEntityId = null,
        object? beforeValue = null, object? afterValue = null,
        string? ipAddress = null, string? userAgent = null, CancellationToken ct = default)
    {
        var log = new AuditLog
        {
            EventType = eventType,
            ActorUserId = actorUserId,
            DocumentId = documentId,
            TargetEntityType = targetEntityType,
            TargetEntityId = targetEntityId,
            BeforeValue = beforeValue is not null ? JsonSerializer.Serialize(beforeValue) : null,
            AfterValue = afterValue is not null ? JsonSerializer.Serialize(afterValue) : null,
            IpAddress = ipAddress,
            UserAgent = userAgent,
            OccurredAt = DateTimeOffset.UtcNow
        };
        await _repo.AddAsync(log, ct);
    }

    public async Task<PagedResult<AuditLogDto>> GetLogsAsync(AuditLogQuery query, CancellationToken ct = default)
    {
        var result = await _repo.QueryAsync(query, ct);
        return new PagedResult<AuditLogDto>
        {
            Items = result.Items.Select(a => new AuditLogDto(
                a.Id, a.EventType, a.ActorUserId,
                a.ActorUser?.Username,
                a.DocumentId,
                a.TargetEntityType, a.TargetEntityId,
                a.BeforeValue is not null ? JsonSerializer.Deserialize<object>(a.BeforeValue) : null,
                a.AfterValue is not null ? JsonSerializer.Deserialize<object>(a.AfterValue) : null,
                a.IpAddress, a.OccurredAt)).ToList(),
            TotalCount = result.TotalCount,
            Page = result.Page,
            PageSize = result.PageSize
        };
    }
}
