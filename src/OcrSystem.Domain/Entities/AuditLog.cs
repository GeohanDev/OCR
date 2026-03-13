namespace OcrSystem.Domain.Entities;

public class AuditLog
{
    public long Id { get; set; }
    public string EventType { get; set; } = string.Empty;
    public Guid? ActorUserId { get; set; }
    public Guid? DocumentId { get; set; }
    public string? TargetEntityType { get; set; }
    public string? TargetEntityId { get; set; }
    public string? BeforeValue { get; set; } // JSON
    public string? AfterValue { get; set; } // JSON
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }
    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;

    public User? ActorUser { get; set; }
    public Document? Document { get; set; }
}
