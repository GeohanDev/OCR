using OcrErpSystem.Application.Commands;
using OcrErpSystem.Application.DTOs;
using OcrErpSystem.Common;

namespace OcrErpSystem.Application.Documents;

public interface IDocumentService
{
    Task<Result<DocumentDto>> UploadAsync(UploadDocumentCommand cmd, CancellationToken ct = default);
    Task<Result<DocumentDto>> GetByIdAsync(Guid id, Guid requestingUserId, string requestingUserRole, CancellationToken ct = default);
    Task<PagedResult<DocumentDto>> ListAsync(DocumentListQuery query, CancellationToken ct = default);
    Task<Result<string>> GetSignedUrlAsync(Guid documentId, Guid requestingUserId, string requestingUserRole, CancellationToken ct = default);
    Task<Result> UpdateStatusAsync(UpdateDocumentStatusCommand cmd, CancellationToken ct = default);
    Task<Result> AssignDocumentTypeAsync(Guid documentId, Guid? documentTypeId, CancellationToken ct = default);
    Task<Result> DeleteAsync(Guid id, Guid requestingUserId, CancellationToken ct = default);
    Task<Result<DocumentDto>> AddVersionAsync(Guid documentId, UploadDocumentCommand cmd, CancellationToken ct = default);
    Task<IReadOnlyList<TrashedDocumentDto>> GetTrashedAsync(Guid? requestingUserId, string requestingUserRole, CancellationToken ct = default);
    Task<Result> RestoreAsync(Guid id, CancellationToken ct = default);
}

public record DocumentListQuery(
    Guid RequestingUserId,
    string RequestingUserRole,
    Guid? BranchId,
    string? Status,
    string? Search,
    DateTimeOffset? FromDate,
    DateTimeOffset? ToDate,
    int Page = 1,
    int PageSize = 20,
    Guid? VendorId = null);
