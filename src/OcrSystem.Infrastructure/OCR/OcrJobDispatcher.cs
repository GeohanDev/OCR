using Microsoft.Extensions.Logging;
using OcrSystem.Application.OCR;

namespace OcrSystem.Infrastructure.OCR;

/// <summary>
/// Thin wrapper invoked by Hangfire. With WorkerCount = 2, two documents can be
/// processed simultaneously. All dependencies are Scoped so each job gets its own
/// DbContext, HttpClients, and token context — no shared mutable state.
/// CancellationToken is intentionally absent because Hangfire cannot serialize it.
/// </summary>
public class OcrJobDispatcher
{
    // Process-wide lock set: prevents the same document from being processed
    // concurrently if it was accidentally enqueued twice (e.g. double-click upload).
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, byte> _inFlight = new();

    private readonly IOcrService _ocr;
    private readonly ILogger<OcrJobDispatcher> _logger;

    public OcrJobDispatcher(IOcrService ocr, ILogger<OcrJobDispatcher> logger)
    {
        _ocr    = ocr;
        _logger = logger;
    }

    public async Task ProcessAsync(Guid documentId)
    {
        if (!_inFlight.TryAdd(documentId, 0))
        {
            _logger.LogWarning(
                "OCR job for document {DocumentId} is already running — skipping duplicate", documentId);
            return;
        }

        try
        {
            await _ocr.ProcessDocumentAsync(documentId, CancellationToken.None);
            // Validation is NOT run automatically after OCR — it requires a live Acumatica
            // token which is only available when the user initiates it via the UI.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OCR job failed for document {DocumentId}", documentId);
            // Errors are already handled inside the services (document status set to Failed).
        }
        finally
        {
            _inFlight.TryRemove(documentId, out _);
        }
    }
}
