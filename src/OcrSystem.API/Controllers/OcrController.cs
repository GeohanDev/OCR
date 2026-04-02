using Hangfire;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OcrSystem.Application.Auth;
using OcrSystem.Application.OCR;
using OcrSystem.Application.Validation;
using OcrSystem.Domain.Enums;
using OcrSystem.Infrastructure.OCR;
using OcrSystem.Infrastructure.Persistence.Repositories;

namespace OcrSystem.API.Controllers;

[ApiController]
[Route("api/ocr")]
[Authorize]
public class OcrController : ControllerBase
{
    private readonly IOcrService _ocr;
    private readonly IValidationService _validation;
    private readonly ICurrentUserContext _user;
    private readonly IBackgroundJobClient _jobs;
    private readonly DocumentRepository _docRepo;

    public OcrController(IOcrService ocr, IValidationService validation, ICurrentUserContext user, IBackgroundJobClient jobs, DocumentRepository docRepo)
    {
        _ocr        = ocr;
        _validation = validation;
        _user       = user;
        _jobs       = jobs;
        _docRepo    = docRepo;
    }

    [HttpPost("{documentId:guid}/process")]
    public async Task<IActionResult> Process(Guid documentId, CancellationToken ct)
    {
        // Mark as PendingProcess immediately so the UI shows the document is queued
        // before Hangfire picks it up and sets it to Processing.
        var doc = await _docRepo.GetByIdAsync(documentId, ct);
        if (doc is not null && doc.Status != DocumentStatus.Processing)
        {
            doc.Status = DocumentStatus.PendingProcess;
            await _docRepo.UpdateAsync(doc, ct);
        }

        // Enqueue into Hangfire so jobs run one at a time (WorkerCount = 1).
        // Returns 202 immediately; the job is persisted in PostgreSQL and
        // will not be lost even if the container restarts mid-queue.
        _jobs.Enqueue<OcrJobDispatcher>(d => d.ProcessAsync(documentId));
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

    [HttpPost("{documentId:guid}/re-extract")]
    public async Task<IActionResult> ReExtractFields(Guid documentId, CancellationToken ct)
    {
        try
        {
            var result = await _ocr.ReExtractFieldsAsync(documentId, ct);
            return Ok(result);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("{documentId:guid}/table-row")]
    public async Task<IActionResult> AddTableRow(Guid documentId, [FromBody] AddTableRowRequest request, CancellationToken ct)
    {
        try
        {
            var columns = request.Columns
                .Select(c => new OcrSystem.Application.OCR.AddTableRowColumn(c.FieldName, c.FieldMappingConfigId))
                .ToList();
            var fields = await _ocr.AddTableRowAsync(documentId, columns, ct);
            return Ok(fields);
        }
        catch (KeyNotFoundException)
        {
            return NotFound("No OCR result found for this document.");
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
public record AddTableRowRequest(IReadOnlyList<AddTableRowColumnDto> Columns);
public record AddTableRowColumnDto(string FieldName, Guid? FieldMappingConfigId);
