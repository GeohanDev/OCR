using OcrErpSystem.Domain.Enums;

namespace OcrErpSystem.Domain.Entities;

public class ValidationResult
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DocumentId { get; set; }
    public Guid? ExtractedFieldId { get; set; }
    public string FieldName { get; set; } = string.Empty;
    public string ValidationType { get; set; } = string.Empty;
    public ValidationStatus Status { get; set; }
    public string? Message { get; set; }
    public string? ErpReference { get; set; } // JSON
    public DateTimeOffset ValidatedAt { get; set; } = DateTimeOffset.UtcNow;

    public Document? Document { get; set; }
    public ExtractedField? ExtractedField { get; set; }
}
