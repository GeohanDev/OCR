using OcrSystem.Domain.Enums;

namespace OcrSystem.Domain.Entities;

public class Document
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? DocumentTypeId { get; set; }
    public string OriginalFilename { get; set; } = string.Empty;
    public string StoragePath { get; set; } = string.Empty;
    public string FileHash { get; set; } = string.Empty;
    public string? MimeType { get; set; }
    public long? FileSizeBytes { get; set; }
    public DocumentStatus Status { get; set; } = DocumentStatus.Uploaded;
    public Guid UploadedBy { get; set; }
    public Guid? BranchId { get; set; }
    public DateTimeOffset UploadedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ProcessedAt { get; set; }
    public Guid? ReviewedBy { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }
    public Guid? ApprovedBy { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }
    public DateTimeOffset? PushedAt { get; set; }
    public string? Notes { get; set; }
    public int CurrentVersion { get; set; } = 1;
    public bool IsDeleted { get; set; } = false;
    public DateTimeOffset? DeletedAt { get; set; }

    /// <summary>FK to local Vendor table. Set automatically after vendor validation passes.</summary>
    public Guid? VendorId { get; set; }

    /// <summary>Denormalized vendor name for quick display without a join.</summary>
    public string? VendorName { get; set; }

    public DocumentType? DocumentType { get; set; }
    public User? UploadedByUser { get; set; }
    public Branch? Branch { get; set; }
    public Vendor? Vendor { get; set; }
    public ICollection<DocumentVersion> Versions { get; set; } = [];
    public ICollection<OcrResult> OcrResults { get; set; } = [];
    public ICollection<ValidationResult> ValidationResults { get; set; } = [];
    public ICollection<AuditLog> AuditLogs { get; set; } = [];
}
