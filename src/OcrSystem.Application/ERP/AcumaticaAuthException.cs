namespace OcrSystem.Application.ERP;

/// <summary>
/// Thrown when Acumatica returns a 401 or 403 response, indicating the forwarded
/// user token has expired or is no longer valid.
/// </summary>
public class AcumaticaAuthException : Exception
{
    public AcumaticaAuthException(string message) : base(message) { }
}
