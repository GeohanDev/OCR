namespace OcrErpSystem.Application.Commands;

public record UploadDocumentCommand(
    Guid DocumentTypeId,
    string OriginalFilename,
    string MimeType,
    long FileSizeBytes,
    Stream FileStream,
    Guid UploadedBy,
    Guid? BranchId);

public record UpdateDocumentStatusCommand(
    Guid DocumentId,
    string NewStatus,
    string? Notes,
    Guid ActorUserId);

public record CorrectFieldCommand(
    Guid ExtractedFieldId,
    string CorrectedValue,
    Guid CorrectedBy);

public record ApproveDocumentCommand(
    Guid DocumentId,
    Guid ApprovedBy,
    string? Notes);

public record RejectDocumentCommand(
    Guid DocumentId,
    Guid RejectedBy,
    string Reason);
