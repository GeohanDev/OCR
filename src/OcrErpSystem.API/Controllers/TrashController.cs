using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OcrErpSystem.Application.Documents;
using OcrErpSystem.Application.FieldMapping;
using OcrErpSystem.Application.Trash;
using System.Security.Claims;

namespace OcrErpSystem.API.Controllers;

[ApiController]
[Route("api/trash")]
[Authorize]
public class TrashController : ControllerBase
{
    private readonly IDocumentService _docs;
    private readonly IFieldMappingService _fields;
    private readonly ITrashPurgeService _purge;

    public TrashController(IDocumentService docs, IFieldMappingService fields, ITrashPurgeService purge)
    {
        _docs = docs; _fields = fields; _purge = purge;
    }

    private Guid? UserId => Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;
    private string UserRole => User.FindFirstValue(ClaimTypes.Role) ?? "Normal";

    [HttpGet("documents")]
    public async Task<IActionResult> GetTrashedDocuments(CancellationToken ct)
    {
        var items = await _docs.GetTrashedAsync(UserId, UserRole, ct);
        return Ok(items);
    }

    [HttpPost("documents/{id:guid}/restore")]
    public async Task<IActionResult> RestoreDocument(Guid id, CancellationToken ct)
    {
        var result = await _docs.RestoreAsync(id, ct);
        return result.IsSuccess ? Ok() : NotFound(result.Error);
    }

    [HttpGet("field-mappings")]
    [Authorize(Policy = "AdminOnly")]
    public async Task<IActionResult> GetTrashedFieldMappings(CancellationToken ct)
    {
        var items = await _fields.GetTrashedFieldMappingsAsync(ct);
        return Ok(items);
    }

    [HttpPost("field-mappings/{id:guid}/restore")]
    [Authorize(Policy = "AdminOnly")]
    public async Task<IActionResult> RestoreFieldMapping(Guid id, CancellationToken ct)
    {
        await _fields.RestoreFieldMappingAsync(id, ct);
        return Ok();
    }

    [HttpGet("document-types")]
    [Authorize(Policy = "AdminOnly")]
    public async Task<IActionResult> GetTrashedDocTypes(CancellationToken ct)
    {
        var items = await _fields.GetTrashedDocTypesAsync(ct);
        return Ok(items);
    }

    [HttpPost("document-types/{id:guid}/restore")]
    [Authorize(Policy = "AdminOnly")]
    public async Task<IActionResult> RestoreDocType(Guid id, CancellationToken ct)
    {
        await _fields.RestoreDocTypeAsync(id, ct);
        return Ok();
    }

    [HttpDelete("purge")]
    [Authorize(Policy = "AdminOnly")]
    public async Task<IActionResult> PurgeAll(CancellationToken ct)
    {
        await _purge.PurgeExpiredAsync(ct);
        return NoContent();
    }
}
