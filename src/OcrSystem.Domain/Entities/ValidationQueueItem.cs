using OcrSystem.Domain.Enums;

namespace OcrSystem.Domain.Entities;

public class ValidationQueueItem
{
    public Guid Id { get; set; }
    public Guid DocumentId { get; set; }
    public string DocumentName { get; set; } = string.Empty;
    public ValidationQueueStatus Status { get; set; } = ValidationQueueStatus.Pending;
    public string? AcumaticaToken { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? ErrorMessage { get; set; }

    public Document? Document { get; set; }
}
