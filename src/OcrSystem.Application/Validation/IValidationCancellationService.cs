namespace OcrSystem.Application.Validation;

/// <summary>
/// Singleton that holds a CancellationTokenSource per document so that
/// a running background validation job can be cancelled from an HTTP request.
/// </summary>
public interface IValidationCancellationService
{
    CancellationToken Register(Guid documentId);
    void Cancel(Guid documentId);
    void Unregister(Guid documentId);
}
