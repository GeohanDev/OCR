using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OcrSystem.Application.ERP;
using OcrSystem.Domain.Entities;
using OcrSystem.Infrastructure.Persistence;
using OcrSystem.Infrastructure.Persistence.Repositories;

namespace OcrSystem.API.Controllers;

[ApiController]
[Route("api/branches")]
[Authorize]
public class BranchesController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IErpIntegrationService _erp;
    private readonly BranchRepository _branchRepo;

    public BranchesController(AppDbContext db, IErpIntegrationService erp, BranchRepository branchRepo)
    {
        _db = db;
        _erp = erp;
        _branchRepo = branchRepo;
    }

    /// <summary>
    /// Returns all active branches synced from Acumatica.
    /// Branches are populated when users are synced (POST /api/users/sync)
    /// or via the dedicated POST /api/branches/sync endpoint.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var branches = await _db.Branches
            .Where(b => b.IsActive)
            .OrderBy(b => b.BranchName)
            .Select(b => new { b.Id, b.BranchCode, b.BranchName, b.AcumaticaBranchId, b.SyncedAt })
            .ToListAsync(ct);

        return Ok(branches);
    }

    /// <summary>Syncs all branches from Acumatica into the local branches table.</summary>
    [HttpPost("sync")]
    [Authorize(Policy = "ManagerAndAbove")]
    public async Task<IActionResult> Sync(CancellationToken ct)
    {
        var erpBranches = await _erp.FetchAllBranchesAsync(ct);
        int upserted = 0;
        foreach (var eb in erpBranches)
        {
            await _branchRepo.UpsertAsync(new Branch
            {
                AcumaticaBranchId = eb.BranchId,
                BranchCode        = eb.BranchCode,
                BranchName        = eb.BranchName,
                IsActive          = eb.IsActive,
                SyncedAt          = DateTimeOffset.UtcNow,
            }, ct);
            upserted++;
        }
        return Ok(new { syncedCount = upserted });
    }

    /// <summary>Returns the raw ERP data for a single branch (by BranchCode) from Acumatica.</summary>
    [HttpGet("{branchCode}/erp")]
    [Authorize(Policy = "ManagerAndAbove")]
    public async Task<IActionResult> GetErpData(string branchCode, CancellationToken ct)
    {
        var result = await _erp.LookupBranchAsync(branchCode, ct);
        if (!result.Found)
            return NotFound(new { message = $"Branch '{branchCode}' not found in Acumatica." });
        return Ok(result.Data);
    }
}
