namespace OcrErpSystem.Application.DTOs;

public record UserDto(
    Guid Id,
    string AcumaticaUserId,
    string Username,
    string DisplayName,
    string? Email,
    string Role,
    Guid? BranchId,
    string? BranchName,
    bool IsActive,
    DateTimeOffset? LastSyncedAt,
    DateTimeOffset CreatedAt);
