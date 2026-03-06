namespace OcrErpSystem.Application.DTOs;

public record DashboardKpisDto(
    int TotalDocuments,
    int PendingReview,
    int Failed,
    int Checked,
    IReadOnlyList<RecentDocumentDto> RecentDocuments);

public record RecentDocumentDto(
    Guid Id,
    string OriginalFilename,
    string Status,
    DateTimeOffset UploadedAt,
    string UploadedByUsername);
