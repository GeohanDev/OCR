using OcrErpSystem.Application.DTOs;
using OcrErpSystem.Common;

namespace OcrErpSystem.Application.Audit;

public interface IAuditService
{
    Task LogAsync(string eventType, Guid? actorUserId, Guid? documentId, string? targetEntityType = null,
        string? targetEntityId = null, object? beforeValue = null, object? afterValue = null,
        string? ipAddress = null, string? userAgent = null, CancellationToken ct = default);

    Task<PagedResult<AuditLogDto>> GetLogsAsync(AuditLogQuery query, CancellationToken ct = default);
}

public record AuditLogQuery(
    Guid? DocumentId,
    Guid? ActorUserId,
    string? EventType,
    DateTimeOffset? From,
    DateTimeOffset? To,
    int Page = 1,
    int PageSize = 50);
