namespace OcrErpSystem.Application.DTOs;

public record DocumentTypeDto(
    Guid Id,
    string TypeKey,
    string DisplayName,
    string PluginClass,
    bool IsActive,
    DateTimeOffset CreatedAt);

public record FieldMappingConfigDto(
    Guid Id,
    Guid DocumentTypeId,
    string FieldName,
    string? DisplayLabel,
    string? RegexPattern,
    string? KeywordAnchor,
    object? PositionRule,
    bool IsRequired,
    string? ErpMappingKey,
    double ConfidenceThreshold,
    int DisplayOrder,
    bool IsActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
