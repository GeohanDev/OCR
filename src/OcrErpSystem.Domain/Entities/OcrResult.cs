namespace OcrErpSystem.Domain.Entities;

public class OcrResult
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DocumentId { get; set; }
    public int VersionNumber { get; set; } = 1;
    public string? RawText { get; set; }
    public string? EngineVersion { get; set; }
    public int? ProcessingMs { get; set; }
    public decimal? OverallConfidence { get; set; }
    public int? PageCount { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public Document? Document { get; set; }
    public ICollection<ExtractedField> ExtractedFields { get; set; } = [];
}
