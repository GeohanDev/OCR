using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using OcrErpSystem.Application.Audit;
using OcrErpSystem.Application.Auth;
using OcrErpSystem.Application.Commands;
using OcrErpSystem.Application.Documents;
using OcrErpSystem.Application.ERP;
using OcrErpSystem.Domain.Enums;

namespace OcrErpSystem.API.Controllers;

[ApiController]
[Route("api/erp")]
[Authorize]
public class ErpController : ControllerBase
{
    private readonly IErpIntegrationService _erp;
    private readonly IDocumentService _documents;
    private readonly IAuditService _audit;
    private readonly ICurrentUserContext _user;
    private readonly IMemoryCache _cache;

    public ErpController(IErpIntegrationService erp, IDocumentService documents, IAuditService audit, ICurrentUserContext user, IMemoryCache cache)
    {
        _erp = erp;
        _documents = documents;
        _audit = audit;
        _user = user;
        _cache = cache;
    }

    [HttpPost("{documentId:guid}/push")]
    [Authorize(Policy = "ManagerAndAbove")]
    public async Task<IActionResult> Push(Guid documentId, CancellationToken ct)
    {
        var doc = await _documents.GetByIdAsync(documentId, _user.UserId, _user.Role, ct);
        if (!doc.IsSuccess) return NotFound(doc.Error);
        if (doc.Value!.Status != DocumentStatus.Approved.ToString())
            return BadRequest("Only approved documents can be pushed to ERP.");

        var result = await _erp.PushDocumentAsync(documentId, ct);
        if (!result.Success) return StatusCode(502, new { message = "ERP push failed.", error = result.ErrorMessage });

        var cmd = new UpdateDocumentStatusCommand(documentId, DocumentStatus.Pushed.ToString(), null, _user.UserId);
        await _documents.UpdateStatusAsync(cmd, ct);

        await _audit.LogAsync("Push", _user.UserId, documentId,
            afterValue: new { status = "Pushed", erpRef = result.AcumaticaReferenceId },
            ipAddress: _user.IpAddress, userAgent: _user.UserAgent, ct: ct);

        return Ok(new { success = true, acumaticaReferenceId = result.AcumaticaReferenceId });
    }

    [HttpGet("lookup/vendors")]
    public async Task<IActionResult> LookupVendor(
        [FromQuery] string? vendorId,
        [FromQuery] string? vendorName,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(vendorId))
        {
            var cacheKey = $"vendor-id:{vendorId.ToUpperInvariant()}";
            if (!_cache.TryGetValue(cacheKey, out var cached))
            {
                var result = await _erp.LookupVendorAsync(vendorId, ct);
                cached = result;
                _cache.Set(cacheKey, cached, TimeSpan.FromMinutes(30));
            }
            return Ok(cached);
        }
        if (!string.IsNullOrWhiteSpace(vendorName))
        {
            var cacheKey = $"vendor-name:{vendorName.ToUpperInvariant()}";
            if (!_cache.TryGetValue(cacheKey, out var cached))
            {
                var result = await _erp.LookupVendorByNameAsync(vendorName, ct);
                cached = result;
                _cache.Set(cacheKey, cached, TimeSpan.FromMinutes(30));
            }
            return Ok(cached);
        }
        return BadRequest("Either vendorId or vendorName query parameter is required.");
    }

    [HttpGet("lookup/ap-invoices")]
    public async Task<IActionResult> LookupApInvoice([FromQuery] string invoiceNbr, CancellationToken ct)
    {
        var cacheKey = $"ap-invoice:{invoiceNbr.ToUpperInvariant()}";
        if (!_cache.TryGetValue(cacheKey, out var cached))
        {
            var result = await _erp.LookupApInvoiceAsync(invoiceNbr, ct);
            cached = result;
            _cache.Set(cacheKey, cached, TimeSpan.FromMinutes(30));
        }
        return Ok(cached);
    }

    [HttpGet("lookup/vendors/list")]
    public async Task<IActionResult> GetAllVendors([FromQuery] int? top, CancellationToken ct)
    {
        var cacheKey = top.HasValue ? $"vendors:top{top}" : "vendors:all";
        if (!_cache.TryGetValue(cacheKey, out var cached))
        {
            var result = await _erp.GetAllVendorsAsync(top, ct);
            // Only cache non-empty results — avoid caching auth failures
            if (result.Count > 0)
                _cache.Set(cacheKey, result, TimeSpan.FromMinutes(30));
            cached = result;
        }
        return Ok(cached);
    }

    [HttpGet("lookup/currencies")]
    public async Task<IActionResult> LookupCurrency([FromQuery] string currencyCode, CancellationToken ct)
    {
        var cacheKey = $"currency:{currencyCode.ToUpperInvariant()}";
        if (!_cache.TryGetValue(cacheKey, out var cached))
        {
            var result = await _erp.LookupCurrencyAsync(currencyCode, ct);
            cached = result;
            _cache.Set(cacheKey, cached, TimeSpan.FromHours(1));
        }
        return Ok(cached);
    }

    [HttpGet("entities")]
    public IActionResult GetEntityCatalog() => Ok(_erp.GetEntityCatalog());

    /// <summary>
    /// Test endpoint for the DynamicErpValidator path — same logic as Entity:Field ERP mapping keys.
    /// Bypasses cache so every call hits Acumatica directly (for debugging).
    /// </summary>
    [HttpGet("lookup/generic")]
    public async Task<IActionResult> LookupGeneric(
        [FromQuery] string entity, [FromQuery] string field, [FromQuery] string value,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(entity) || string.IsNullOrWhiteSpace(field) || string.IsNullOrWhiteSpace(value))
            return BadRequest("entity, field and value are all required.");
        var result = await _erp.LookupGenericAsync(entity, field, value, ct);
        return Ok(result);
    }

    /// <summary>
    /// Diagnostic probe: fetches the first record from any Acumatica entity (no filter, $top=1).
    /// Useful for verifying endpoint path, auth, and discovering actual field names.
    /// </summary>
    [HttpGet("probe/{entity}")]
    public async Task<IActionResult> ProbeEntity(string entity, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(entity)) return BadRequest("entity is required.");
        var result = await _erp.ProbeEntityAsync(entity, ct);
        return Ok(result);
    }

    [HttpGet("lookup/branches")]
    public async Task<IActionResult> LookupBranch([FromQuery] string branchCode, CancellationToken ct)
    {
        var cacheKey = $"branch:{branchCode.ToUpperInvariant()}";
        if (!_cache.TryGetValue(cacheKey, out var cached))
        {
            var result = await _erp.LookupBranchAsync(branchCode, ct);
            cached = result;
            _cache.Set(cacheKey, cached, TimeSpan.FromHours(1));
        }
        return Ok(cached);
    }
}
