using Microsoft.Extensions.Logging;
using OcrSystem.Application.Auth;
using OcrSystem.Application.Validation;
using OcrSystem.Domain.Enums;
using OcrSystem.Infrastructure.Persistence.Repositories;

namespace OcrSystem.Infrastructure.Validation;

/// <summary>
/// Thin Hangfire job wrapper for full-document validation.
/// Accepts the caller's Acumatica token so ERP validators can authenticate
/// even though there is no HTTP request context in a background job.
/// </summary>
public class ValidationJobDispatcher
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, byte> _inFlight = new();

    private readonly IValidationService _validation;
    private readonly IAcumaticaTokenContext _tokenContext;
    private readonly DocumentRepository _docRepo;
    private readonly ValidationQueueRepository _queueRepo;
    private readonly IValidationCancellationService _cancellation;
    private readonly ILogger<ValidationJobDispatcher> _logger;

    public ValidationJobDispatcher(
        IValidationService validation,
        IAcumaticaTokenContext tokenContext,
        DocumentRepository docRepo,
        ValidationQueueRepository queueRepo,
        IValidationCancellationService cancellation,
        ILogger<ValidationJobDispatcher> logger)
    {
        _validation   = validation;
        _tokenContext = tokenContext;
        _docRepo      = docRepo;
        _queueRepo    = queueRepo;
        _cancellation = cancellation;
        _logger       = logger;
    }

    /// <summary>
    /// Called by Hangfire for table-only validation (re-validates AllowMultiple rows only,
    /// preserving existing header field results).
    /// </summary>
    public async Task ValidateTableAsync(Guid documentId, string? acumaticaToken, Guid? queueItemId = null)
    {
        if (!_inFlight.TryAdd(documentId, 0))
        {
            _logger.LogWarning(
                "Table validation job for document {DocumentId} is already running — skipping duplicate", documentId);
            return;
        }

        var ct = _cancellation.Register(documentId);
        var failed = false;
        var cancelled = false;
        try
        {
            if (queueItemId.HasValue)
            {
                var item = await _queueRepo.GetByIdAsync(queueItemId.Value);
                if (item is not null)
                {
                    item.Status    = ValidationQueueStatus.Processing;
                    item.StartedAt = DateTimeOffset.UtcNow;
                    await _queueRepo.UpdateAsync(item);
                }
            }

            if (!string.IsNullOrWhiteSpace(acumaticaToken))
                _tokenContext.ForwardedToken = acumaticaToken;

            await _validation.ValidateTableRowsAsync(documentId, ct);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
            _logger.LogInformation("Table validation cancelled for document {DocumentId}", documentId);

            if (queueItemId.HasValue)
            {
                var item = await _queueRepo.GetByIdAsync(queueItemId.Value);
                if (item is not null)
                {
                    item.Status      = ValidationQueueStatus.Cancelled;
                    item.CompletedAt = DateTimeOffset.UtcNow;
                    await _queueRepo.UpdateAsync(item);
                }
            }
        }
        catch (Exception ex)
        {
            failed = true;
            _logger.LogError(ex, "Background table validation failed for document {DocumentId}", documentId);

            if (queueItemId.HasValue)
            {
                var item = await _queueRepo.GetByIdAsync(queueItemId.Value);
                if (item is not null)
                {
                    item.Status       = ValidationQueueStatus.Failed;
                    item.ErrorMessage = ex.Message;
                    item.CompletedAt  = DateTimeOffset.UtcNow;
                    await _queueRepo.UpdateAsync(item);
                }
            }
        }
        finally
        {
            _cancellation.Unregister(documentId);
            _inFlight.TryRemove(documentId, out _);
            await _docRepo.SetValidatingAsync(documentId, false);

            if (!failed && !cancelled && queueItemId.HasValue)
            {
                var item = await _queueRepo.GetByIdAsync(queueItemId.Value);
                if (item is not null)
                {
                    item.Status      = ValidationQueueStatus.Completed;
                    item.CompletedAt = DateTimeOffset.UtcNow;
                    await _queueRepo.UpdateAsync(item);
                }
            }
        }
    }

    /// <summary>
    /// Called by Hangfire. <paramref name="acumaticaToken"/> is the forwarded
    /// ERP JWT captured from the HTTP request that enqueued this job.
    /// </summary>
    public async Task ValidateAsync(Guid documentId, string? acumaticaToken, Guid? queueItemId = null)
    {
        if (!_inFlight.TryAdd(documentId, 0))
        {
            _logger.LogWarning(
                "Validation job for document {DocumentId} is already running — skipping duplicate", documentId);
            return;
        }

        var ct = _cancellation.Register(documentId);
        var failed = false;
        var cancelled = false;
        try
        {
            if (queueItemId.HasValue)
            {
                var item = await _queueRepo.GetByIdAsync(queueItemId.Value);
                if (item is not null)
                {
                    item.Status = ValidationQueueStatus.Processing;
                    item.StartedAt = DateTimeOffset.UtcNow;
                    await _queueRepo.UpdateAsync(item);
                }
            }

            if (!string.IsNullOrWhiteSpace(acumaticaToken))
                _tokenContext.ForwardedToken = acumaticaToken;

            await _validation.ValidateDocumentAsync(documentId, ct);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
            _logger.LogInformation("Validation cancelled for document {DocumentId}", documentId);

            if (queueItemId.HasValue)
            {
                var item = await _queueRepo.GetByIdAsync(queueItemId.Value);
                if (item is not null)
                {
                    item.Status = ValidationQueueStatus.Cancelled;
                    item.CompletedAt = DateTimeOffset.UtcNow;
                    await _queueRepo.UpdateAsync(item);
                }
            }
        }
        catch (Exception ex)
        {
            failed = true;
            _logger.LogError(ex, "Background validation failed for document {DocumentId}", documentId);

            if (queueItemId.HasValue)
            {
                var item = await _queueRepo.GetByIdAsync(queueItemId.Value);
                if (item is not null)
                {
                    item.Status = ValidationQueueStatus.Failed;
                    item.ErrorMessage = ex.Message;
                    item.CompletedAt = DateTimeOffset.UtcNow;
                    await _queueRepo.UpdateAsync(item);
                }
            }
        }
        finally
        {
            _cancellation.Unregister(documentId);
            _inFlight.TryRemove(documentId, out _);
            await _docRepo.SetValidatingAsync(documentId, false);

            if (!failed && !cancelled && queueItemId.HasValue)
            {
                var item = await _queueRepo.GetByIdAsync(queueItemId.Value);
                if (item is not null)
                {
                    item.Status = ValidationQueueStatus.Completed;
                    item.CompletedAt = DateTimeOffset.UtcNow;
                    await _queueRepo.UpdateAsync(item);
                }
            }
        }
    }
}
