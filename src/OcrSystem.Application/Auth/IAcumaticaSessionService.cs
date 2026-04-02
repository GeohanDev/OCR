namespace OcrSystem.Application.Auth;

/// <summary>
/// Tracks per-user Acumatica session activity.
/// A session is "started" by the keepalive endpoint after a successful Acumatica ping.
/// Any request with a forwarded Acumatica token refreshes the 10-minute sliding window.
/// After 10 minutes of inactivity the session is considered timed-out and the next ERP
/// call will fail with 424 so the frontend can prompt the user to re-authenticate.
/// </summary>
public interface IAcumaticaSessionService
{
    /// <summary>Mark the session as explicitly started (called after a successful keepalive ping).</summary>
    void StartSession(Guid userId);

    /// <summary>Refresh the 10-minute sliding window without requiring a full re-start.</summary>
    void RecordActivity(Guid userId);

    /// <summary>
    /// Returns true when the session was previously started but has since expired due to
    /// inactivity (sliding window elapsed).  Returns false for users who have never started
    /// a session (e.g. first request after login) so ERP calls are not blocked on first use.
    /// </summary>
    bool IsTimedOut(Guid userId);

    /// <summary>Remove all session state for the user (called on explicit logout).</summary>
    void EndSession(Guid userId);
}
