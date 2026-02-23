namespace OcrErpSystem.Domain.Entities;

public class DocumentVersion
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DocumentId { get; set; }
    public int VersionNumber { get; set; }
    public string StoragePath { get; set; } = string.Empty;
    public string FileHash { get; set; } = string.Empty;
    public Guid UploadedBy { get; set; }
    public DateTimeOffset UploadedAt { get; set; } = DateTimeOffset.UtcNow;

    public Document? Document { get; set; }
    public User? UploadedByUser { get; set; }
}
