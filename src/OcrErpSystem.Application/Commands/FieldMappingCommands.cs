namespace OcrErpSystem.Application.Commands;

public record RegisterDocumentTypeCommand(
    string TypeKey,
    string DisplayName,
    string PluginClass);

public record CreateFieldMappingCommand(
    Guid DocumentTypeId,
    string FieldName,
    string? DisplayLabel,
    string? RegexPattern,
    string? KeywordAnchor,
    string? PositionRule,
    bool IsRequired,
    string? ErpMappingKey,
    double ConfidenceThreshold,
    int DisplayOrder);

public record UpdateFieldMappingCommand(
    Guid FieldMappingId,
    string? DisplayLabel,
    string? RegexPattern,
    string? KeywordAnchor,
    string? PositionRule,
    bool IsRequired,
    string? ErpMappingKey,
    double ConfidenceThreshold,
    int DisplayOrder,
    bool IsActive);
