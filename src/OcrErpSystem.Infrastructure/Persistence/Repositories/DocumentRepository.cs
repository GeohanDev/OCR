using Microsoft.EntityFrameworkCore;
using OcrErpSystem.Application.Documents;
using OcrErpSystem.Common;
using OcrErpSystem.Domain.Entities;
using OcrErpSystem.Domain.Enums;

namespace OcrErpSystem.Infrastructure.Persistence.Repositories;

public class DocumentRepository
{
    private readonly AppDbContext _db;

    public DocumentRepository(AppDbContext db) => _db = db;

    public async Task<Document?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        await _db.Documents
            .Include(d => d.DocumentType)
            .Include(d => d.UploadedByUser)
            .Include(d => d.Branch)
            .FirstOrDefaultAsync(d => d.Id == id, ct);

    public async Task<Document?> GetByHashAsync(string fileHash, CancellationToken ct = default) =>
        await _db.Documents.FirstOrDefaultAsync(d => d.FileHash == fileHash, ct);

    public async Task<PagedResult<Document>> ListAsync(DocumentListQuery query, CancellationToken ct = default)
    {
        var q = _db.Documents
            .Include(d => d.DocumentType)
            .Include(d => d.UploadedByUser)
            .Include(d => d.Branch)
            .AsQueryable();

        bool isManager = query.RequestingUserRole.Equals("Manager", StringComparison.OrdinalIgnoreCase);
        bool isAdmin = query.RequestingUserRole.Equals("Admin", StringComparison.OrdinalIgnoreCase);

        if (!isManager && !isAdmin)
            q = q.Where(d => d.UploadedBy == query.RequestingUserId);
        else if (isManager && query.BranchId.HasValue)
            q = q.Where(d => d.BranchId == query.BranchId);

        if (!string.IsNullOrWhiteSpace(query.Status) && Enum.TryParse<DocumentStatus>(query.Status, out var status))
            q = q.Where(d => d.Status == status);

        if (!string.IsNullOrWhiteSpace(query.Search))
            q = q.Where(d => d.OriginalFilename.Contains(query.Search));

        if (query.FromDate.HasValue)
            q = q.Where(d => d.UploadedAt >= query.FromDate.Value);

        if (query.ToDate.HasValue)
            q = q.Where(d => d.UploadedAt <= query.ToDate.Value);

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
}
