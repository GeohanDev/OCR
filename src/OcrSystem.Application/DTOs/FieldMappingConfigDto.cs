namespace OcrSystem.Application.DTOs;

public record DocumentTypeDto(
    Guid Id,
    string TypeKey,
    string DisplayName,
    string PluginClass,
    string Category,
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
    bool AllowMultiple,
    string? ErpMappingKey,
    string? ErpResponseField,
    double ConfidenceThreshold,
    int DisplayOrder,
    bool IsActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? DependentFieldKey = null,
    bool IsManualEntry = false,
    bool IsCheckbox = false);
