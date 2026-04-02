using System.Text;
using System.Text.Json;
using Hangfire;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OcrSystem.Application.Audit;
using OcrSystem.Application.Auth;
using OcrSystem.Application.Commands;
using OcrSystem.Application.Documents;
using OcrSystem.Application.Validation;
using OcrSystem.Domain.Entities;
using OcrSystem.Domain.Enums;
using OcrSystem.Application.ERP;
using OcrSystem.Infrastructure.Persistence.Repositories;
using OcrSystem.Infrastructure.Validation;

namespace OcrSystem.API.Controllers;

[ApiController]
[Route("api/validation")]
[Authorize]
public class ValidationController : ControllerBase
{
    private readonly IValidationService _validation;
    private readonly IDocumentService _documents;
    private readonly IAuditService _audit;
    private readonly ICurrentUserContext _user;
    private readonly IAcumaticaTokenContext _tokenContext;
    private readonly IBackgroundJobClient _jobs;
    private readonly DocumentRepository _docRepo;
    private readonly ValidationQueueRepository _queueRepo;
    private readonly IValidationCancellationService _cancellation;

    public ValidationController(
        IValidationService validation,
        IDocumentService documents,
        IAuditService audit,
        ICurrentUserContext user,
        IAcumaticaTokenContext tokenContext,
        IBackgroundJobClient jobs,
        DocumentRepository docRepo,
        ValidationQueueRepository queueRepo,
        IValidationCancellationService cancellation)
    {
        _validation = validation;
        _documents = documents;
        _audit = audit;
        _user = user;
        _tokenContext = tokenContext;
        _jobs = jobs;
        _docRepo = docRepo;
        _queueRepo = queueRepo;
        _cancellation = cancellation;
    }

    /// <summary>
    /// Returns true when a forwarded Acumatica JWT is present and its exp claim is in the past.
    /// Returns false if absent (no ERP calls needed) or if the token cannot be decoded.
    /// </summary>
    private bool IsAcumaticaSessionExpired()
    {
        var token = _tokenContext.ForwardedToken;
        if (string.IsNullOrWhiteSpace(token)) return false;
        try
        {
            var parts = token.Split('.');
            if (parts.Length < 2) return false;
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload += (payload.Length % 4) switch { 2 => "==", 3 => "=", _ => "" };
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(payload));
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("exp", out var expEl))
            {
                var exp = DateTimeOffset.FromUnixTimeSeconds(expEl.GetInt64());
                return exp < DateTimeOffset.UtcNow;
            }
            return false;
        }
        catch { return false; }
    }

    private IActionResult? AcumaticaSessionCheck()
    {
        if (!IsAcumaticaSessionExpired()) return null;
        return StatusCode(424, new
        {
            acumaticaSessionExpired = true,
            message = "Acumatica session has expired. Please sign out and sign in again to refresh your ERP connection."
        });
    }

    [HttpPost("{documentId:guid}/run")]
    public async Task<IActionResult> Run(Guid documentId, CancellationToken ct)
    {
        var sessionError = AcumaticaSessionCheck();
        if (sessionError is not null) return sessionError;

        try
        {
            var summary = await _validation.ValidateDocumentAsync(documentId, ct);
            await _audit.LogAsync("ValidationRun", _user.UserId, documentId,
                afterValue: new { summary.TotalFields, summary.PassedCount, summary.FailedCount },
                ipAddress: _user.IpAddress, userAgent: _user.UserAgent, ct: ct);
            return Ok(summary);
        }
        catch (AcumaticaAuthException)
        {
            return StatusCode(424, new { acumaticaSessionExpired = true,
                message = "Acumatica session has expired. Please sign out and sign in again." });
        }
    }

    [HttpPost("{documentId:guid}/enqueue")]
    public async Task<IActionResult> Enqueue(Guid documentId, CancellationToken ct)
    {
        var sessionError = AcumaticaSessionCheck();
        if (sessionError is not null) return sessionError;

        var doc = await _docRepo.GetByIdAsync(documentId, ct);
        if (doc is null) return NotFound();

        var token = _tokenContext.ForwardedToken;

        var queueItem = new ValidationQueueItem
        {
            DocumentId   = documentId,
            DocumentName = doc.OriginalFilename,
            AcumaticaToken = token,
            CreatedAt    = DateTimeOffset.UtcNow
        };
        await _queueRepo.AddAsync(queueItem, ct);

        await _docRepo.SetValidatingAsync(documentId, true, ct);

        _jobs.Enqueue<ValidationJobDispatcher>(d => d.ValidateAsync(documentId, token, queueItem.Id));

        return Accepted(new { status = "queued", documentId, queueItemId = queueItem.Id });
    }

    [HttpPost("{documentId:guid}/enqueue-table")]
    public async Task<IActionResult> EnqueueTable(Guid documentId, CancellationToken ct)
    {
        var sessionError = AcumaticaSessionCheck();
        if (sessionError is not null) return sessionError;

        var doc = await _docRepo.GetByIdAsync(documentId, ct);
        if (doc is null) return NotFound();

        var token = _tokenContext.ForwardedToken;

        var queueItem = new ValidationQueueItem
        {
            DocumentId    = documentId,
            DocumentName  = doc.OriginalFilename,
            AcumaticaToken = token,
            CreatedAt     = DateTimeOffset.UtcNow
        };
        await _queueRepo.AddAsync(queueItem, ct);
        await _docRepo.SetValidatingAsync(documentId, true, ct);

        _jobs.Enqueue<ValidationJobDispatcher>(d => d.ValidateTableAsync(documentId, token, queueItem.Id));

        return Accepted(new { status = "queued", documentId, queueItemId = queueItem.Id });
    }

    [HttpPost("{documentId:guid}/stop")]
    public async Task<IActionResult> Stop(Guid documentId, CancellationToken ct)
    {
        _cancellation.Cancel(documentId);
        await _docRepo.SetValidatingAsync(documentId, false, ct);
        await _queueRepo.CancelPendingAsync(documentId, ct);
        return Ok(new { status = "stopped" });
    }

    [HttpGet("queue")]
    public async Task<IActionResult> GetQueue(CancellationToken ct)
    {
        var items = await _queueRepo.GetRecentAsync(20, ct);
        return Ok(items.Select(i => new
        {
            i.Id,
            i.DocumentId,
            i.DocumentName,
            status = i.Status.ToString(),
            i.CreatedAt,
            i.StartedAt,
            i.CompletedAt,
            i.ErrorMessage
        }));
    }

    [HttpPost("{documentId:guid}/field/{fieldId:guid}")]
    public async Task<IActionResult> ValidateField(Guid documentId, Guid fieldId, CancellationToken ct)
    {
        var sessionError = AcumaticaSessionCheck();
        if (sessionError is not null) return sessionError;

        try
        {
            var results = await _validation.ValidateFieldAsync(documentId, fieldId, ct);
            return Ok(results);
        }
        catch (AcumaticaAuthException)
        {
            return StatusCode(424, new { acumaticaSessionExpired = true,
                message = "Acumatica session has expired. Please sign out and sign in again." });
        }
    }

    [HttpPost("{documentId:guid}/row")]
    public async Task<IActionResult> ValidateRow(
        Guid documentId,
        [FromBody] ValidateRowRequest request,
        CancellationToken ct)
    {
        var sessionError = AcumaticaSessionCheck();
        if (sessionError is not null) return sessionError;

        try
        {
            var results = await _validation.ValidateRowAsync(documentId, request.FieldIds, ct);
            return Ok(results);
        }
        catch (AcumaticaAuthException)
        {
            return StatusCode(424, new { acumaticaSessionExpired = true,
                message = "Acumatica session has expired. Please sign out and sign in again." });
        }
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

public record ValidateRowRequest(IReadOnlyList<Guid> FieldIds);
