using Microsoft.EntityFrameworkCore;
using OcrSystem.Application.Documents;
using OcrSystem.Common;
using OcrSystem.Domain.Entities;
using OcrSystem.Domain.Enums;

namespace OcrSystem.Infrastructure.Persistence.Repositories;

public class DocumentRepository
{
    private readonly AppDbContext _db;

    public DocumentRepository(AppDbContext db) => _db = db;

    public async Task<Document?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        await _db.Documents
            .Include(d => d.DocumentType)
            .Include(d => d.UploadedByUser)
            .Include(d => d.Branch)
            .Include(d => d.Vendor)
            .FirstOrDefaultAsync(d => d.Id == id, ct);

    public async Task<Document?> GetByHashAsync(string fileHash, Guid uploadedBy, CancellationToken ct = default) =>
        await _db.Documents.FirstOrDefaultAsync(
            d => d.FileHash == fileHash && d.UploadedBy == uploadedBy, ct);

    public async Task<PagedResult<Document>> ListAsync(DocumentListQuery query, CancellationToken ct = default)
    {
        var q = _db.Documents
            .Include(d => d.DocumentType)
            .Include(d => d.UploadedByUser)
            .Include(d => d.Branch)
            .Include(d => d.Vendor)
            .AsQueryable();

        bool isAdmin   = query.RequestingUserRole.Equals("Admin",   StringComparison.OrdinalIgnoreCase);
        bool isManager = query.RequestingUserRole.Equals("Manager", StringComparison.OrdinalIgnoreCase);

        // Branch restricts by document's own branch_id OR, for historical docs without one,
        // by the uploading user's branch (covers docs uploaded before branch sync).
        if (isAdmin)
        {
            // Admins see all documents — no branch restriction.
        }
        else if (query.BranchId.HasValue)
        {
            var bid = query.BranchId.Value;
            q = q.Where(d => d.BranchId == bid ||
                             (d.BranchId == null &&
                              _db.Users.Any(u => u.Id == d.UploadedBy && u.BranchId == bid)));
        }
        // else: BranchId is null ("All Branches") → no restriction.

        if (!string.IsNullOrWhiteSpace(query.Status) && Enum.TryParse<DocumentStatus>(query.Status, out var status))
            q = q.Where(d => d.Status == status);

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var pattern = $"%{query.Search}%";
            q = q.Where(d =>
                EF.Functions.ILike(d.OriginalFilename, pattern) ||
                (d.VendorName != null && EF.Functions.ILike(d.VendorName, pattern)));
        }

        if (query.FromDate.HasValue)
            q = q.Where(d => d.UploadedAt >= query.FromDate.Value);

        if (query.ToDate.HasValue)
            q = q.Where(d => d.UploadedAt <= query.ToDate.Value);

        if (query.VendorId.HasValue)
            q = q.Where(d => d.VendorId == query.VendorId.Value);

        if (!string.IsNullOrWhiteSpace(query.VendorName))
            q = q.Where(d => d.VendorName != null && EF.Functions.ILike(d.VendorName, $"%{query.VendorName}%"));

        if (query.FilterBranchId.HasValue)
        {
            var fid = query.FilterBranchId.Value;
            q = q.Where(d => d.BranchId == fid ||
                             (d.BranchId == null &&
                              _db.Users.Any(u => u.Id == d.UploadedBy && u.BranchId == fid)));
        }

        if (query.DocumentTypeId.HasValue)
            q = q.Where(d => d.DocumentTypeId == query.DocumentTypeId.Value);

        var total = await q.CountAsync(ct);
        var items = await q
            .OrderByDescending(d => d.UploadedAt)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .ToListAsync(ct);

        return new PagedResult<Document> { Items = items, TotalCount = total, Page = query.Page, PageSize = query.PageSize };
    }

    public async Task AddAsync(Document document, CancellationToken ct = default)
    {
        await _db.Documents.AddAsync(document, ct);
        await _db.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(Document document, CancellationToken ct = default)
    {
        _db.Documents.Update(document);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<int> CountByStatusAsync(DocumentStatus status, Guid? branchId, Guid? userId, CancellationToken ct = default)
    {
        var q = _db.Documents.Where(d => d.Status == status);
        if (branchId.HasValue) q = q.Where(d => d.BranchId == branchId);
        if (userId.HasValue) q = q.Where(d => d.UploadedBy == userId);
        return await q.CountAsync(ct);
    }

    public async Task<List<Document>> GetTrashedAsync(Guid? requestingUserId, string requestingUserRole, CancellationToken ct = default)
    {
        var q = _db.Documents.IgnoreQueryFilters()
            .Include(d => d.DocumentType)
            .Include(d => d.UploadedByUser)
            .Where(d => d.IsDeleted);
        bool isAdmin = requestingUserRole.Equals("Admin", StringComparison.OrdinalIgnoreCase);
        if (!isAdmin && requestingUserId.HasValue)
            q = q.Where(d => d.UploadedBy == requestingUserId.Value);
        return await q.OrderByDescending(d => d.DeletedAt).ToListAsync(ct);
    }

    public async Task<bool> RestoreAsync(Guid id, CancellationToken ct = default)
    {
        var doc = await _db.Documents.IgnoreQueryFilters().FirstOrDefaultAsync(d => d.Id == id, ct);
        if (doc is null || !doc.IsDeleted) return false;
        doc.IsDeleted = false;
        doc.DeletedAt = null;
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task SetValidatingAsync(Guid id, bool isValidating, CancellationToken ct = default)
    {
        await _db.Documents
            .Where(d => d.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.IsValidating, isValidating), ct);
    }

    /// <summary>
    /// Sets branch_id on all unassigned documents uploaded by the given user.
    /// Called when a user's branch is assigned or changed via user management or sync.
    /// Only fills in documents that have no branch yet to avoid overwriting explicit assignments.
    /// </summary>
    public async Task UpdateBranchForUserAsync(Guid userId, Guid? branchId, CancellationToken ct = default)
    {
        if (branchId is null) return; // null = All Branches — don't wipe existing assignments
        await _db.Documents
            .Where(d => d.UploadedBy == userId && d.BranchId == null && !d.IsDeleted)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.BranchId, branchId), ct);
    }

    public async Task<int> PurgeExpiredAsync(DateTimeOffset cutoff, CancellationToken ct = default)
    {
        var expired = await _db.Documents.IgnoreQueryFilters()
            .Where(d => d.IsDeleted && d.DeletedAt.HasValue && d.DeletedAt.Value < cutoff)
            .ToListAsync(ct);
        _db.Documents.RemoveRange(expired);
        await _db.SaveChangesAsync(ct);
        return expired.Count;
    }
}
