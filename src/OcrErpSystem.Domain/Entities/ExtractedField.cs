namespace OcrErpSystem.Domain.Entities;

public class ExtractedField
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OcrResultId { get; set; }
    public Guid? FieldMappingConfigId { get; set; }
    public int SortOrder { get; set; }
    public string FieldName { get; set; } = string.Empty;
    public string? RawValue { get; set; }
    public string? NormalizedValue { get; set; }
    public decimal? Confidence { get; set; }
    public string? BoundingBox { get; set; } // JSON
    public bool IsManuallyCorreected { get; set; } = false;
    public string? CorrectedValue { get; set; }
    public Guid? CorrectedBy { get; set; }
    public DateTimeOffset? CorrectedAt { get; set; }

    public OcrResult? OcrResult { get; set; }
    public FieldMappingConfig? FieldMappingConfig { get; set; }
    public User? CorrectedByUser { get; set; }
    public ICollection<ValidationResult> ValidationResults { get; set; } = [];
}
