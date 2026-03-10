using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OcrErpSystem.Application.Auth;
using OcrErpSystem.Application.OCR;
using OcrErpSystem.Application.Validation;

namespace OcrErpSystem.API.Controllers;

[ApiController]
[Route("api/ocr")]
[Authorize]
public class OcrController : ControllerBase
{
    private readonly IOcrService _ocr;
    private readonly IValidationService _validation;
    private readonly ICurrentUserContext _user;

    public OcrController(IOcrService ocr, IValidationService validation, ICurrentUserContext user)
    {
        _ocr = ocr;
        _validation = validation;
        _user = user;
    }

    [HttpPost("{documentId:guid}/process")]
    public async Task<IActionResult> Process(Guid documentId, CancellationToken ct)
    {
        try
        {
            var result = await _ocr.ProcessDocumentAsync(documentId, ct);
            // Auto-run validation after every OCR run so results are always fresh.
            await _validation.ValidateDocumentAsync(documentId, ct);
            return Ok(result);
        }
        catch (KeyNotFoundException)
        {
            return NotFound("Document not found.");
        }
    }

    [HttpGet("{documentId:guid}/result")]
    public async Task<IActionResult> GetResult(Guid documentId, CancellationToken ct)
    {
        var result = await _ocr.GetResultAsync(documentId, ct);
        if (result is null) return NotFound("No OCR result for this document.");
        return Ok(result);
    }

    [HttpGet("{documentId:guid}/raw-text")]
    public async Task<IActionResult> GetRawText(Guid documentId, CancellationToken ct)
    {
        var text = await _ocr.GetRawTextAsync(documentId, ct);
        if (text is null) return NotFound("No OCR result for this document.");
        return Ok(new { rawText = text });
    }

    [HttpDelete("{documentId:guid}/fields/{fieldId:guid}")]
    public async Task<IActionResult> DeleteField(Guid documentId, Guid fieldId, CancellationToken ct)
    {
        try
        {
            await _ocr.DeleteFieldAsync(fieldId, ct);
            return NoContent();
        }
        catch (KeyNotFoundException)
        {
            return NotFound("Field not found.");
        }
    }

    [HttpPatch("{documentId:guid}/fields/{fieldId:guid}")]
    public async Task<IActionResult> CorrectField(Guid documentId, Guid fieldId, [FromBody] CorrectFieldRequest request, CancellationToken ct)
    {
        try
        {
            var result = await _ocr.CorrectFieldAsync(fieldId, request.CorrectedValue, _user.UserId, ct);
            return Ok(result);
        }
        catch (KeyNotFoundException)
        {
            return NotFound("Field not found.");
        }
    }
}

public record CorrectFieldRequest(string CorrectedValue);
