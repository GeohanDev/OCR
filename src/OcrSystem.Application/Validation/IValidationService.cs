using OcrSystem.Application.DTOs;

namespace OcrSystem.Application.Validation;

public interface IValidationService
{
    Task<ValidationSummary> ValidateDocumentAsync(Guid documentId, CancellationToken ct = default);
    Task<ValidationSummary> ValidateTableRowsAsync(Guid documentId, CancellationToken ct = default);
    Task<IReadOnlyList<ValidationResultDto>> ValidateFieldAsync(Guid documentId, Guid extractedFieldId, CancellationToken ct = default);
    Task<IReadOnlyList<ValidationResultDto>> ValidateRowAsync(Guid documentId, IReadOnlyList<Guid> fieldIds, CancellationToken ct = default);
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
