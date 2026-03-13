using OcrSystem.Application.Commands;
using OcrSystem.Application.Documents;
using OcrSystem.Application.DTOs;
using OcrSystem.Application.Storage;
using OcrSystem.Common;
using OcrSystem.Domain.Entities;
using OcrSystem.Domain.Enums;
using OcrSystem.Infrastructure.Persistence.Repositories;

namespace OcrSystem.Infrastructure.Services;

public class DocumentService : IDocumentService
{
    private readonly DocumentRepository _repo;
    private readonly IFileStorageService _storage;

    public DocumentService(DocumentRepository repo, IFileStorageService storage)
    {
        _repo = repo;
        _storage = storage;
    }

    private static readonly HashSet<string> AllowedMimeTypes =
    [
        "application/pdf", "image/png", "image/jpeg", "image/tiff"
    ];

    private static string NormalizeMimeType(string? mimeType, string filename)
    {
        // Browser may send application/octet-stream or empty for PDF/image files;
        // derive the correct type from the file extension as a fallback.
        if (!string.IsNullOrEmpty(mimeType) &&
            mimeType != "application/octet-stream" &&
            mimeType != "application/x-www-form-urlencoded")
            return mimeType;

        return Path.GetExtension(filename).ToLowerInvariant() switch
        {
            ".pdf"              => "application/pdf",
            ".png"              => "image/png",
            ".jpg" or ".jpeg"   => "image/jpeg",
            ".tif" or ".tiff"   => "image/tiff",
            _                   => mimeType ?? "application/octet-stream"
        };
    }

    public async Task<Result<DocumentDto>> UploadAsync(UploadDocumentCommand cmd, CancellationToken ct = default)
    {
        var mimeType = NormalizeMimeType(cmd.MimeType, cmd.OriginalFilename);

        if (!AllowedMimeTypes.Contains(mimeType))
            return Result<DocumentDto>.Failure(
                $"Unsupported file type '{mimeType}'. Only PDF, PNG, JPG, and TIFF files are accepted.",
                ErrorCodes.InvalidFileType);

        var hash = await _storage.ComputeHashAsync(cmd.FileStream, ct);
        var existing = await _repo.GetByHashAsync(hash, cmd.UploadedBy, ct);
        if (existing is not null)
            return Result<DocumentDto>.Failure("Duplicate document detected.", ErrorCodes.DuplicateDocument);

        var storagePath = await _storage.StoreAsync(cmd.FileStream, cmd.OriginalFilename, mimeType, ct);
        var doc = new Document
        {
            DocumentTypeId = cmd.DocumentTypeId == Guid.Empty ? null : cmd.DocumentTypeId,
            OriginalFilename = cmd.OriginalFilename,
            StoragePath = storagePath,
            FileHash = hash,
            MimeType = mimeType,
            FileSizeBytes = cmd.FileSizeBytes,
            Status = DocumentStatus.Uploaded,
            UploadedBy = cmd.UploadedBy,
            BranchId = cmd.BranchId,
            UploadedAt = DateTimeOffset.UtcNow
        };
        await _repo.AddAsync(doc, ct);
        return Result<DocumentDto>.Success(MapToDto(doc));
    }

    public async Task<Result<DocumentDto>> GetByIdAsync(Guid id, Guid requestingUserId, string requestingUserRole, CancellationToken ct = default)
    {
        var doc = await _repo.GetByIdAsync(id, ct);
        if (doc is null) return Result<DocumentDto>.Failure("Document not found.", ErrorCodes.NotFound);
        if (!CanAccess(doc, requestingUserId, requestingUserRole))
            return Result<DocumentDto>.Failure("Access denied.", ErrorCodes.Forbidden);
        return Result<DocumentDto>.Success(MapToDto(doc));
    }

    public async Task<PagedResult<DocumentDto>> ListAsync(DocumentListQuery query, CancellationToken ct = default)
    {
        var result = await _repo.ListAsync(query, ct);
        return new PagedResult<DocumentDto>
        {
            Items = result.Items.Select(MapToDto).ToList(),
            TotalCount = result.TotalCount,
            Page = result.Page,
            PageSize = result.PageSize
        };
    }

    public async Task<Result<string>> GetSignedUrlAsync(Guid documentId, Guid requestingUserId, string requestingUserRole, CancellationToken ct = default)
    {
        var doc = await _repo.GetByIdAsync(documentId, ct);
        if (doc is null) return Result<string>.Failure("Document not found.", ErrorCodes.NotFound);
        if (!CanAccess(doc, requestingUserId, requestingUserRole))
            return Result<string>.Failure("Access denied.", ErrorCodes.Forbidden);
        var url = await _storage.GenerateSignedUrlAsync(doc.StoragePath, TimeSpan.FromMinutes(30), ct);
        return Result<string>.Success(url);
    }

    public async Task<Result> AssignDocumentTypeAsync(Guid documentId, Guid? documentTypeId, CancellationToken ct = default)
    {
        var doc = await _repo.GetByIdAsync(documentId, ct);
        if (doc is null) return Result.Failure("Document not found.", ErrorCodes.NotFound);
        doc.DocumentTypeId = documentTypeId;
        await _repo.UpdateAsync(doc, ct);
        return Result.Success();
    }

    public async Task<Result> UpdateStatusAsync(UpdateDocumentStatusCommand cmd, CancellationToken ct = default)
    {
        var doc = await _repo.GetByIdAsync(cmd.DocumentId, ct);
        if (doc is null) return Result.Failure("Document not found.", ErrorCodes.NotFound);
        if (!Enum.TryParse<DocumentStatus>(cmd.NewStatus, out var newStatus))
            return Result.Failure("Invalid status.", ErrorCodes.InvalidStatus);
        doc.Status = newStatus;
        if (cmd.Notes is not null) doc.Notes = cmd.Notes;
        await _repo.UpdateAsync(doc, ct);
        return Result.Success();
    }

    public async Task<Result> DeleteAsync(Guid id, Guid requestingUserId, CancellationToken ct = default)
    {
        var doc = await _repo.GetByIdAsync(id, ct);
        if (doc is null) return Result.Failure("Document not found.", ErrorCodes.NotFound);
        doc.IsDeleted = true;
        doc.DeletedAt = DateTimeOffset.UtcNow;
        await _repo.UpdateAsync(doc, ct);
        return Result.Success();
    }

    public async Task<IReadOnlyList<TrashedDocumentDto>> GetTrashedAsync(Guid? requestingUserId, string requestingUserRole, CancellationToken ct = default)
    {
        var docs = await _repo.GetTrashedAsync(requestingUserId, requestingUserRole, ct);
        return docs.Select(d => new TrashedDocumentDto(
            d.Id, d.OriginalFilename, d.DocumentType?.DisplayName, d.Status.ToString(),
            d.UploadedByUser?.Username ?? d.UploadedBy.ToString(),
            d.UploadedAt, d.DeletedAt ?? d.UploadedAt)).ToList();
    }

    public async Task<Result> RestoreAsync(Guid id, CancellationToken ct = default)
    {
        var ok = await _repo.RestoreAsync(id, ct);
        return ok ? Result.Success() : Result.Failure("Document not found in trash.", ErrorCodes.NotFound);
    }

    public async Task<Result<DocumentDto>> AddVersionAsync(Guid documentId, UploadDocumentCommand cmd, CancellationToken ct = default)
    {
        var doc = await _repo.GetByIdAsync(documentId, ct);
        if (doc is null) return Result<DocumentDto>.Failure("Document not found.", ErrorCodes.NotFound);

        var mimeType = NormalizeMimeType(cmd.MimeType, cmd.OriginalFilename);
        if (!AllowedMimeTypes.Contains(mimeType))
            return Result<DocumentDto>.Failure(
                $"Unsupported file type '{mimeType}'. Only PDF, PNG, JPG, and TIFF files are accepted.",
                ErrorCodes.InvalidFileType);

        var hash = await _storage.ComputeHashAsync(cmd.FileStream, ct);
        var storagePath = await _storage.StoreAsync(cmd.FileStream, cmd.OriginalFilename, mimeType, ct);

        doc.CurrentVersion++;
        doc.StoragePath = storagePath;
        doc.FileHash = hash;
        doc.Status = DocumentStatus.Uploaded;
        doc.Versions.Add(new DocumentVersion
        {
            DocumentId = documentId,
            VersionNumber = doc.CurrentVersion,
            StoragePath = storagePath,
            FileHash = hash,
            UploadedBy = cmd.UploadedBy,
            UploadedAt = DateTimeOffset.UtcNow
        });
        await _repo.UpdateAsync(doc, ct);
        return Result<DocumentDto>.Success(MapToDto(doc));
    }

    private static bool CanAccess(Document doc, Guid userId, string role)
    {
        if (role.Equals("Admin", StringComparison.OrdinalIgnoreCase)) return true;
        if (role.Equals("Manager", StringComparison.OrdinalIgnoreCase)) return true;
        return doc.UploadedBy == userId;
    }

    private static DocumentDto MapToDto(Document d) => new(
        d.Id, d.DocumentTypeId, d.DocumentType?.DisplayName,
        d.OriginalFilename, d.MimeType, d.FileSizeBytes,
        d.Status.ToString(), d.UploadedBy, d.UploadedByUser?.Username ?? "",
        d.BranchId, d.Branch?.BranchName,
        d.UploadedAt, d.ProcessedAt, d.ReviewedAt, d.ApprovedAt, d.PushedAt,
        d.Notes, d.CurrentVersion,
        d.VendorId, d.VendorName);
}
