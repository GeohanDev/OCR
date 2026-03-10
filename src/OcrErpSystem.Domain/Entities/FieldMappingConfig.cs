namespace OcrErpSystem.Domain.Entities;

public class FieldMappingConfig
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DocumentTypeId { get; set; }
    public string FieldName { get; set; } = string.Empty;
    public string? DisplayLabel { get; set; }
    public string? RegexPattern { get; set; }
    public string? KeywordAnchor { get; set; }
    public string? PositionRule { get; set; } // JSON
    public bool IsRequired { get; set; } = false;
    public bool AllowMultiple { get; set; } = false;
    public string? ErpMappingKey { get; set; }
    public string? ErpResponseField { get; set; }  // JSON key in ERP response to show on pass (e.g. "vendorId", "RefNbr")
    public string? DependentFieldKey { get; set; } // FieldName this field's ERP check must cross-validate against (e.g. "vendorName" for invoice cross-check)
    public decimal ConfidenceThreshold { get; set; } = 0.75m;
    public int DisplayOrder { get; set; } = 0;
    public bool IsManualEntry { get; set; } = false;
    public bool IsCheckbox { get; set; } = false;
    public bool IsActive { get; set; } = true;
    public bool IsDeleted { get; set; } = false;
    public DateTimeOffset? DeletedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DocumentType? DocumentType { get; set; }
}
