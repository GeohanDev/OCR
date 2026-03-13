using Microsoft.EntityFrameworkCore;
using OcrSystem.Domain.Entities;

namespace OcrSystem.Infrastructure.Persistence.Repositories;

public class UserRepository
{
    private readonly AppDbContext _db;

    public UserRepository(AppDbContext db) => _db = db;

    public async Task<User?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        await _db.Users.Include(u => u.Branch).FirstOrDefaultAsync(u => u.Id == id, ct);

    public async Task<User?> GetByAcumaticaIdAsync(string acumaticaUserId, CancellationToken ct = default) =>
        await _db.Users.FirstOrDefaultAsync(u => u.AcumaticaUserId == acumaticaUserId, ct);

    public async Task<User?> GetByUsernameAsync(string username, CancellationToken ct = default) =>
        await _db.Users.Include(u => u.Branch).FirstOrDefaultAsync(u => u.Username == username, ct);

    public async Task<List<User>> GetAllAsync(CancellationToken ct = default) =>
        await _db.Users.Include(u => u.Branch).OrderBy(u => u.Username).ToListAsync(ct);

    public async Task<List<User>> GetPagedAsync(int page, int pageSize, CancellationToken ct = default) =>
        await _db.Users.Include(u => u.Branch)
            .OrderBy(u => u.Username)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

    public async Task<int> CountAsync(CancellationToken ct = default) =>
        await _db.Users.CountAsync(ct);

    public async Task UpsertAsync(User user, CancellationToken ct = default)
    {
        var existing = await GetByAcumaticaIdAsync(user.AcumaticaUserId, ct);
        if (existing is null)
            await _db.Users.AddAsync(user, ct);
        else
        {
            existing.Username = user.Username;
            existing.DisplayName = user.DisplayName;
            existing.Email = user.Email;
            // Role is managed exclusively by admins via PATCH /users/{id}/role — never overwrite.
            existing.BranchId = user.BranchId;
            existing.IsActive = user.IsActive;
            existing.LastSyncedAt = user.LastSyncedAt;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
        }
        await _db.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(User user, CancellationToken ct = default)
    {
        _db.Users.Update(user);
        await _db.SaveChangesAsync(ct);
    }
}
