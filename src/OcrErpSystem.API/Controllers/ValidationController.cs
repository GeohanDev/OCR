using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OcrErpSystem.Application.Audit;
using OcrErpSystem.Application.Auth;
using OcrErpSystem.Application.Commands;
using OcrErpSystem.Application.Documents;
using OcrErpSystem.Application.Validation;
using OcrErpSystem.Domain.Enums;

namespace OcrErpSystem.API.Controllers;

[ApiController]
[Route("api/validation")]
[Authorize]
public class ValidationController : ControllerBase
{
    private readonly IValidationService _validation;
    private readonly IDocumentService _documents;
    private readonly IAuditService _audit;
    private readonly ICurrentUserContext _user;

    public ValidationController(IValidationService validation, IDocumentService documents, IAuditService audit, ICurrentUserContext user)
    {
        _validation = validation;
        _documents = documents;
        _audit = audit;
        _user = user;
    }

    [HttpPost("{documentId:guid}/run")]
    public async Task<IActionResult> Run(Guid documentId, CancellationToken ct)
    {
        var summary = await _validation.ValidateDocumentAsync(documentId, ct);
        await _audit.LogAsync("ValidationRun", _user.UserId, documentId,
            afterValue: new { summary.TotalFields, summary.PassedCount, summary.FailedCount },
            ipAddress: _user.IpAddress, userAgent: _user.UserAgent, ct: ct);
        return Ok(summary);
    }

    [HttpGet("{documentId:guid}/results")]
    public async Task<IActionResult> GetResults(Guid documentId, CancellationToken ct)
    {
        var results = await _validation.GetValidationResultsAsync(documentId, ct);
        return Ok(results);
    }

    [HttpPost("{documentId:guid}/approve")]
    [Authorize(Policy = "ManagerAndAbove")]
    public async Task<IActionResult> Approve(Guid documentId, [FromBody] ApproveRejectRequest request, CancellationToken ct)
    {
        var eligibility = await _validation.CheckApprovalEligibilityAsync(documentId, ct);
        if (!eligibility.CanApprove)
            return UnprocessableEntity(new { message = "Cannot approve: required fields failed validation.", blockingFields = eligibility.BlockingFieldNames });

        var cmd = new UpdateDocumentStatusCommand(documentId, DocumentStatus.Approved.ToString(), request.Notes, _user.UserId);
        var result = await _documents.UpdateStatusAsync(cmd, ct);
        if (!result.IsSuccess) return BadRequest(result.Error);

        await _audit.LogAsync("Approval", _user.UserId, documentId,
            afterValue: new { status = "Approved", notes = request.Notes },
            ipAddress: _user.IpAddress, userAgent: _user.UserAgent, ct: ct);
        return Ok(new { message = "Document approved." });
    }

    [HttpPost("{documentId:guid}/reject")]
    [Authorize(Policy = "ManagerAndAbove")]
    public async Task<IActionResult> Reject(Guid documentId, [FromBody] ApproveRejectRequest request, CancellationToken ct)
    {
        var cmd = new UpdateDocumentStatusCommand(documentId, DocumentStatus.Rejected.ToString(), request.Notes, _user.UserId);
        var result = await _documents.UpdateStatusAsync(cmd, ct);
        if (!result.IsSuccess) return BadRequest(result.Error);

        await _audit.LogAsync("Rejection", _user.UserId, documentId,
            afterValue: new { status = "Rejected", reason = request.Notes },
            ipAddress: _user.IpAddress, userAgent: _user.UserAgent, ct: ct);
        return Ok(new { message = "Document rejected." });
    }
}

public record ApproveRejectRequest(string? Notes);
