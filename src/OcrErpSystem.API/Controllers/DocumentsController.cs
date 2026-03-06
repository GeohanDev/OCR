using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OcrErpSystem.Application.Auth;
using OcrErpSystem.Application.Commands;
using OcrErpSystem.Application.Documents;

namespace OcrErpSystem.API.Controllers;

[ApiController]
[Route("api/documents")]
[Authorize]
public class DocumentsController : ControllerBase
{
    private readonly IDocumentService _documents;
    private readonly ICurrentUserContext _user;

    public DocumentsController(IDocumentService documents, ICurrentUserContext user)
    {
        _documents = documents;
        _user = user;
    }

    [HttpPost("upload")]
    public async Task<IActionResult> Upload([FromForm] UploadDocumentRequest request, CancellationToken ct)
    {
        if (request.Files is null || request.Files.Count == 0)
            return BadRequest("No files provided.");

        var results = new List<object>();
        foreach (var file in request.Files)
        {
            var cmd = new UploadDocumentCommand(
                request.DocumentTypeId,
                file.FileName,
                file.ContentType,
                file.Length,
                file.OpenReadStream(),
                _user.UserId,
                _user.BranchId);

            var result = await _documents.UploadAsync(cmd, ct);
            if (result.IsSuccess)
                results.Add(new { success = true, document = result.Value });
            else
                results.Add(new { success = false, error = result.Error, file = file.FileName });
        }
        return Ok(results);
    }

    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] string? status,
        [FromQuery] string? search,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        var query = new DocumentListQuery(
            _user.UserId, _user.Role, _user.BranchId,
            status, search, from, to, page, pageSize);
        var result = await _documents.ListAsync(query, ct);
        return Ok(result);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id, CancellationToken ct)
    {
        var result = await _documents.GetByIdAsync(id, _user.UserId, _user.Role, ct);
        if (!result.IsSuccess) return result.ErrorCode == "NOT_FOUND" ? NotFound(result.Error) : Forbid();
        return Ok(result.Value);
    }

    [HttpGet("{id:guid}/file")]
    public async Task<IActionResult> GetSignedUrl(Guid id, CancellationToken ct)
    {
        var result = await _documents.GetSignedUrlAsync(id, _user.UserId, _user.Role, ct);
        if (!result.IsSuccess) return result.ErrorCode == "NOT_FOUND" ? NotFound(result.Error) : Forbid();
        return Ok(new { url = result.Value });
    }

    [HttpPatch("{id:guid}/type")]
    public async Task<IActionResult> AssignDocumentType(Guid id, [FromBody] AssignDocumentTypeRequest request, CancellationToken ct)
    {
        var result = await _documents.AssignDocumentTypeAsync(id, request.DocumentTypeId, ct);
        if (!result.IsSuccess) return NotFound(result.Error);
        return NoContent();
    }

    [HttpPatch("{id:guid}/status")]
    [Authorize(Policy = "ManagerAndAbove")]
    public async Task<IActionResult> UpdateStatus(Guid id, [FromBody] UpdateStatusRequest request, CancellationToken ct)
    {
        var cmd = new UpdateDocumentStatusCommand(id, request.Status, request.Notes, _user.UserId);
        var result = await _documents.UpdateStatusAsync(cmd, ct);
        if (!result.IsSuccess) return BadRequest(result.Error);
        return NoContent();
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = "AdminOnly")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var result = await _documents.DeleteAsync(id, _user.UserId, ct);
        if (!result.IsSuccess) return NotFound(result.Error);
        return NoContent();
    }

    [HttpPost("{id:guid}/versions")]
    public async Task<IActionResult> AddVersion(Guid id, [FromForm] AddVersionRequest request, CancellationToken ct)
    {
        if (request.File is null) return BadRequest("No file provided.");
        var cmd = new UploadDocumentCommand(
            Guid.Empty, request.File.FileName, request.File.ContentType,
            request.File.Length, request.File.OpenReadStream(), _user.UserId, _user.BranchId);
        var result = await _documents.AddVersionAsync(id, cmd, ct);
        if (!result.IsSuccess) return BadRequest(result.Error);
        return Ok(result.Value);
    }
}

public record UploadDocumentRequest(
    [FromForm] Guid DocumentTypeId,
    [FromForm] IFormFileCollection? Files);

public record UpdateStatusRequest(string Status, string? Notes);

public record AddVersionRequest([FromForm] IFormFile? File);

public record AssignDocumentTypeRequest(Guid? DocumentTypeId);
