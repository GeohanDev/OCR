using Microsoft.EntityFrameworkCore;
using OcrSystem.Domain.Entities;

namespace OcrSystem.Infrastructure.Persistence.Repositories;

public class BranchRepository
{
    private readonly AppDbContext _db;

    public BranchRepository(AppDbContext db) => _db = db;

    public async Task<List<Branch>> GetAllAsync(CancellationToken ct = default) =>
        await _db.Branches.Where(b => b.IsActive).OrderBy(b => b.BranchName).ToListAsync(ct);

    public async Task<Branch?> GetByAcumaticaIdAsync(string acumaticaBranchId, CancellationToken ct = default) =>
        await _db.Branches.FirstOrDefaultAsync(b => b.AcumaticaBranchId == acumaticaBranchId, ct);

    public async Task UpsertAsync(Branch branch, CancellationToken ct = default)
    {
        var existing = await GetByAcumaticaIdAsync(branch.AcumaticaBranchId, ct);
        if (existing is null)
            await _db.Branches.AddAsync(branch, ct);
        else
        {
            existing.BranchCode = branch.BranchCode;
            existing.BranchName = branch.BranchName;
            existing.IsActive = branch.IsActive;
            existing.SyncedAt = DateTimeOffset.UtcNow;
        }
        await _db.SaveChangesAsync(ct);
    }
}
