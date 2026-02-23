namespace OcrErpSystem.Domain.Entities;

public class Branch
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string AcumaticaBranchId { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;
    public string BranchName { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public DateTimeOffset SyncedAt { get; set; } = DateTimeOffset.UtcNow;

    public ICollection<User> Users { get; set; } = [];
    public ICollection<Document> Documents { get; set; } = [];
}
