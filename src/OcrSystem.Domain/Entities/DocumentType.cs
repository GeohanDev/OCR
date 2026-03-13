using OcrSystem.Domain.Enums;

namespace OcrSystem.Domain.Entities;

public class DocumentType
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TypeKey { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string PluginClass { get; set; } = string.Empty;
    public DocumentCategory Category { get; set; } = DocumentCategory.General;
    public bool IsActive { get; set; } = true;
    public bool IsDeleted { get; set; } = false;
    public DateTimeOffset? DeletedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public ICollection<FieldMappingConfig> FieldMappingConfigs { get; set; } = [];
    public ICollection<Document> Documents { get; set; } = [];
}
