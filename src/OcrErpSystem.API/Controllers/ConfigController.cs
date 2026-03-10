using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OcrErpSystem.Application.Commands;
using OcrErpSystem.Application.FieldMapping;

namespace OcrErpSystem.API.Controllers;

[ApiController]
[Route("api/config")]
[Authorize]
public class ConfigController : ControllerBase
{
    private readonly IFieldMappingService _fieldMapping;

    public ConfigController(IFieldMappingService fieldMapping) => _fieldMapping = fieldMapping;

    [HttpGet("document-types")]
    public async Task<IActionResult> GetDocumentTypes(CancellationToken ct)
    {
        var types = await _fieldMapping.GetDocumentTypesAsync(ct);
        return Ok(types);
    }

    [HttpPost("document-types")]
    [Authorize(Policy = "AdminOnly")]
    public async Task<IActionResult> RegisterDocumentType([FromBody] RegisterDocumentTypeCommand cmd, CancellationToken ct)
    {
        var dt = await _fieldMapping.RegisterDocumentTypeAsync(cmd, ct);
        return CreatedAtAction(nameof(GetDocumentTypes), new { }, dt);
    }

    [HttpDelete("document-types/{typeId:guid}")]
    [Authorize(Policy = "AdminOnly")]
    public async Task<IActionResult> DeleteDocumentType(Guid typeId, CancellationToken ct)
    {
        await _fieldMapping.DeleteDocumentTypeAsync(typeId, ct);
        return NoContent();
    }

    [HttpGet("document-types/{typeId:guid}/fields")]
    public async Task<IActionResult> GetFieldMappings(Guid typeId, CancellationToken ct)
    {
        var configs = await _fieldMapping.GetFieldMappingsAsync(typeId, ct);
        return Ok(configs);
    }

    [HttpPost("document-types/{typeId:guid}/fields")]
    [Authorize(Policy = "AdminOnly")]
    public async Task<IActionResult> CreateFieldMapping(Guid typeId, [FromBody] CreateFieldMappingCommand cmd, CancellationToken ct)
    {
        var actualCmd = cmd with { DocumentTypeId = typeId };
        var config = await _fieldMapping.CreateFieldMappingAsync(actualCmd, ct);
        return CreatedAtAction(nameof(GetFieldMappings), new { typeId }, config);
    }

    [HttpPut("document-types/{typeId:guid}/fields/{fieldId:guid}")]
    [Authorize(Policy = "AdminOnly")]
    public async Task<IActionResult> UpdateFieldMapping(Guid typeId, Guid fieldId, [FromBody] UpdateFieldMappingCommand cmd, CancellationToken ct)
    {
        try
        {
            var actualCmd = cmd with { FieldMappingId = fieldId };
            var config = await _fieldMapping.UpdateFieldMappingAsync(actualCmd, ct);
            return Ok(config);
        }
        catch (KeyNotFoundException e)
        {
            return NotFound(e.Message);
        }
    }

    [HttpDelete("document-types/{typeId:guid}/fields/{fieldId:guid}")]
    [Authorize(Policy = "AdminOnly")]
    public async Task<IActionResult> DeleteFieldMapping(Guid typeId, Guid fieldId, CancellationToken ct)
    {
        await _fieldMapping.DeleteFieldMappingAsync(fieldId, ct);
        return NoContent();
    }

    [HttpPost("document-types/{typeId:guid}/fields/reorder")]
    [Authorize(Policy = "AdminOnly")]
    public async Task<IActionResult> ReorderFieldMappings(Guid typeId, [FromBody] List<Guid> orderedIds, CancellationToken ct)
    {
        await _fieldMapping.ReorderFieldMappingsAsync(typeId, orderedIds, ct);
        return NoContent();
    }
}
