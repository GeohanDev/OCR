using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OcrErpSystem.Application.ERP;
using OcrErpSystem.Infrastructure.ERP;
using OcrErpSystem.Infrastructure.Persistence.Repositories;
using AcumaticaAuthException = OcrErpSystem.Application.ERP.AcumaticaAuthException;

namespace OcrErpSystem.API.Controllers;

[ApiController]
[Route("api/vendors")]
[Authorize(Policy = "ManagerAndAbove")]
public class VendorController : ControllerBase
{
    private readonly VendorRepository _repo;
    private readonly IVendorSyncService _syncService;

    public VendorController(VendorRepository repo, IVendorSyncService syncService)
    {
        _repo = repo;
        _syncService = syncService;
    }

    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] string? search,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        var result = await _repo.ListAsync(search, page, pageSize, ct);
        return Ok(new
        {
            items = result.Items.Select(v => new
            {
                id              = v.Id,
                acumaticaVendorId = v.AcumaticaVendorId,
                vendorName      = v.VendorName,
                addressLine1    = v.AddressLine1,
                addressLine2    = v.AddressLine2,
                city            = v.City,
                state           = v.State,
                postalCode      = v.PostalCode,
                country         = v.Country,
                paymentTerms    = v.PaymentTerms,
                isActive        = v.IsActive,
                lastSyncedAt    = v.LastSyncedAt,
            }),
            totalCount  = result.TotalCount,
            page        = result.Page,
            pageSize    = result.PageSize,
            totalPages  = result.TotalPages,
            hasNextPage = result.HasNextPage,
            hasPreviousPage = result.HasPreviousPage,
        });
    }

    [HttpPost("sync")]
    public async Task<IActionResult> Sync(CancellationToken ct)
    {
        try
        {
            var count = await _syncService.SyncVendorsAsync(ct);
            return Ok(new { success = true, syncedCount = count });
        }
        catch (AcumaticaAuthException ex)
        {
            return StatusCode(424, new { message = ex.Message });
        }
        catch (Exception ex)
        {
            return StatusCode(502, new { message = "Vendor sync failed.", error = ex.Message });
        }
    }
}
