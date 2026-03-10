namespace OcrErpSystem.Application.DTOs;

public record DocumentDto(
    Guid Id,
    Guid? DocumentTypeId,
    string? DocumentTypeName,
    string OriginalFilename,
    string? MimeType,
    long? FileSizeBytes,
    string Status,
    Guid UploadedBy,
    string UploadedByUsername,
    Guid? BranchId,
    string? BranchName,
    DateTimeOffset UploadedAt,
    DateTimeOffset? ProcessedAt,
    DateTimeOffset? ReviewedAt,
    DateTimeOffset? ApprovedAt,
    DateTimeOffset? PushedAt,
    string? Notes,
    int CurrentVersion,
    Guid? VendorId = null,
    string? VendorName = null);
