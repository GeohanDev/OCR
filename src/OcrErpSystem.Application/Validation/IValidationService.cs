using OcrErpSystem.Application.DTOs;

namespace OcrErpSystem.Application.Validation;

public interface IValidationService
{
    Task<ValidationSummary> ValidateDocumentAsync(Guid documentId, CancellationToken ct = default);
    Task<IReadOnlyList<ValidationResultDto>> GetValidationResultsAsync(Guid documentId, CancellationToken ct = default);
    Task<ApprovalEligibility> CheckApprovalEligibilityAsync(Guid documentId, CancellationToken ct = default);
}

public record ValidationSummary(
    Guid DocumentId,
    int TotalFields,
    int PassedCount,
    int FailedCount,
    int WarningCount,
    bool CanApprove,
    IReadOnlyList<ValidationResultDto> Results);

public record ApprovalEligibility(bool CanApprove, IReadOnlyList<string> BlockingFieldNames);
