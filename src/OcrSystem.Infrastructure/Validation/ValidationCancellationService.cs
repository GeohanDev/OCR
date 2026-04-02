using System.Collections.Concurrent;
using OcrSystem.Application.Validation;

namespace OcrSystem.Infrastructure.Validation;

public class ValidationCancellationService : IValidationCancellationService
{
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _sources = new();

    public CancellationToken Register(Guid documentId)
    {
        // Replace any stale source for this document.
        if (_sources.TryRemove(documentId, out var old))
            old.Dispose();

        var cts = new CancellationTokenSource();
        _sources[documentId] = cts;
        return cts.Token;
    }

    public void Cancel(Guid documentId)
    {
        if (_sources.TryGetValue(documentId, out var cts))
            cts.Cancel();
    }

    public void Unregister(Guid documentId)
    {
        if (_sources.TryRemove(documentId, out var cts))
            cts.Dispose();
    }
}
