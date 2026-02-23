using OcrErpSystem.Domain.Enums;

namespace OcrErpSystem.Domain.Entities;

public class User
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string AcumaticaUserId { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? Email { get; set; }
    public UserRole Role { get; set; } = UserRole.Normal;
    public Guid? BranchId { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset? LastSyncedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public Branch? Branch { get; set; }
    public ICollection<Document> UploadedDocuments { get; set; } = [];
    public ICollection<AuditLog> AuditLogs { get; set; } = [];
}
