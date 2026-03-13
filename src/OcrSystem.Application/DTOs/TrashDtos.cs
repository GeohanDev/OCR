namespace OcrSystem.Application.DTOs;

public record TrashedDocumentDto(
    Guid Id,
    string OriginalFilename,
    string? DocumentTypeName,
    string Status,
    string UploadedByUsername,
    DateTimeOffset UploadedAt,
    DateTimeOffset DeletedAt);

public record TrashedFieldConfigDto(
    Guid Id,
    Guid DocumentTypeId,
    string DocumentTypeName,
    string FieldName,
    string? DisplayLabel,
    DateTimeOffset DeletedAt);

public record TrashedDocTypeDto(
    Guid Id,
    string TypeKey,
    string DisplayName,
    DateTimeOffset DeletedAt,
    int FieldCount);
