namespace OcrSystem.Application.DTOs;

public record AuditLogDto(
    long Id,
    string EventType,
    Guid? ActorUserId,
    string? ActorUsername,
    Guid? DocumentId,
    string? TargetEntityType,
    string? TargetEntityId,
    object? BeforeValue,
    object? AfterValue,
    string? IpAddress,
    DateTimeOffset OccurredAt);
