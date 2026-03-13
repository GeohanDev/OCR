using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OcrSystem.Application.Auth;
using OcrSystem.Application.OCR;
using OcrSystem.Application.Validation;

namespace OcrSystem.API.Controllers;

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
    public IActionResult Process(Guid documentId, [FromServices] IServiceScopeFactory scopeFactory)
    {
        // Fire-and-forget: return 202 immediately so the frontend is never blocked.
        // A new DI scope is created for the background work so services are not
        // accessed after the request scope is disposed.
        _ = Task.Run(async () =>
        {
            using var scope = scopeFactory.CreateScope();
            var ocr = scope.ServiceProvider.GetRequiredService<IOcrService>();
            var validation = scope.ServiceProvider.GetRequiredService<IValidationService>();
            try
            {
                await ocr.ProcessDocumentAsync(documentId, CancellationToken.None);
                await validation.ValidateDocumentAsync(documentId, CancellationToken.None);
            }
            catch
            {
                // Errors are already handled inside the service (document status set to Failed).
            }
        });
        return Accepted();
    }

    [HttpGet("{documentId:guid}/result")]
    public async Task<IActionResult> GetResult(Guid documentId, CancellationToken ct)
    {
        var result = await _ocr.GetResultAsync(documentId, ct);
        if (result is null) return NotFound("No OCR result for this document.");
        return Ok(result);
    }

    [HttpPost("{documentId:guid}/paddle-raw")]
    [Authorize(Policy = "AdminOnly")]
    public async Task<IActionResult> RunPaddleRaw(Guid documentId, CancellationToken ct)
    {
        try
        {
            var rawText = await _ocr.RunPaddleOcrRawAsync(documentId, ct);
            return Ok(new { rawText });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
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
