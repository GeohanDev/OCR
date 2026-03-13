using OcrSystem.Domain.Enums;

namespace OcrSystem.Application.Commands;

public record RegisterDocumentTypeCommand(
    string TypeKey,
    string DisplayName,
    string PluginClass,
    DocumentCategory Category = DocumentCategory.General);

public record UpdateDocumentTypeCommand(
    Guid DocumentTypeId,
    string DisplayName,
    string Category);

public record CreateFieldMappingCommand(
    Guid DocumentTypeId,
    string FieldName,
    string? DisplayLabel,
    string? RegexPattern,
    string? KeywordAnchor,
    string? PositionRule,
    bool IsRequired,
    bool AllowMultiple,
    string? ErpMappingKey,
    string? ErpResponseField,
    double ConfidenceThreshold,
    int DisplayOrder,
    string? DependentFieldKey = null,
    bool IsManualEntry = false,
    bool IsCheckbox = false);

public record UpdateFieldMappingCommand(
    Guid FieldMappingId,
    string? DisplayLabel,
    string? RegexPattern,
    string? KeywordAnchor,
    string? PositionRule,
    bool IsRequired,
    bool AllowMultiple,
    string? ErpMappingKey,
    string? ErpResponseField,
    double ConfidenceThreshold,
    int DisplayOrder,
    string? DependentFieldKey = null,
    bool IsManualEntry = false,
    bool IsCheckbox = false);
