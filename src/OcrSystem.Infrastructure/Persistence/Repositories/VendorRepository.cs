using Microsoft.EntityFrameworkCore;
using OcrSystem.Common;
using OcrSystem.Domain.Entities;

namespace OcrSystem.Infrastructure.Persistence.Repositories;

public class VendorRepository
{
    private readonly AppDbContext _db;

    public VendorRepository(AppDbContext db) => _db = db;

    public async Task<Vendor?> GetByAcumaticaIdAsync(string acumaticaVendorId, CancellationToken ct = default) =>
        await _db.Vendors.FirstOrDefaultAsync(v => v.AcumaticaVendorId == acumaticaVendorId, ct);

    public async Task<Vendor?> GetByNameAsync(string vendorName, CancellationToken ct = default)
    {
        var normalizedInput = NormalizeName(vendorName);
        var all = await _db.Vendors.ToListAsync(ct);
        return all.FirstOrDefault(v =>
            string.Equals(NormalizeName(v.VendorName), normalizedInput, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<PagedResult<Vendor>> ListAsync(string? search, int page, int pageSize, CancellationToken ct = default)
    {
        var q = _db.Vendors.AsQueryable();
        if (!string.IsNullOrWhiteSpace(search))
            q = q.Where(v => v.VendorName.Contains(search) || v.AcumaticaVendorId.Contains(search));

        var total = await q.CountAsync(ct);
        var items = await q
            .OrderBy(v => v.VendorName)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return new PagedResult<Vendor> { Items = items, TotalCount = total, Page = page, PageSize = pageSize };
    }

    public async Task UpsertAsync(Vendor vendor, CancellationToken ct = default)
    {
        var existing = await GetByAcumaticaIdAsync(vendor.AcumaticaVendorId, ct);
        if (existing is null)
        {
            await _db.Vendors.AddAsync(vendor, ct);
        }
        else
        {
            existing.VendorName   = vendor.VendorName;
            existing.AddressLine1 = vendor.AddressLine1;
            existing.AddressLine2 = vendor.AddressLine2;
            existing.City         = vendor.City;
            existing.State        = vendor.State;
            existing.PostalCode   = vendor.PostalCode;
            existing.Country      = vendor.Country;
            existing.PaymentTerms = vendor.PaymentTerms;
            existing.IsActive     = vendor.IsActive;
            existing.LastSyncedAt = DateTimeOffset.UtcNow;
            existing.UpdatedAt    = DateTimeOffset.UtcNow;
        }
        await _db.SaveChangesAsync(ct);
    }

    public async Task<int> CountAsync(CancellationToken ct = default) =>
        await _db.Vendors.CountAsync(ct);

    private static string NormalizeName(string s) =>
        string.Join(" ", s.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
