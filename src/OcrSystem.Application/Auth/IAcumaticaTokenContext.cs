namespace OcrSystem.Application.Auth;

public interface IAcumaticaTokenContext
{
    string? ForwardedToken { get; set; }

    /// <summary>
    /// Set by AcumaticaJwtMiddleware when the user had a session that has since
    /// timed out due to inactivity (10 minutes).  AcumaticaClient throws
    /// AcumaticaAuthException when this is true → controller returns 424 →
    /// frontend prompts re-authentication. The keepalive endpoint clears this flag.
    /// </summary>
    bool SessionTimedOut { get; set; }
}
