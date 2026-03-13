namespace OcrSystem.Domain.Enums;

public static class AuditEventType
{
    public const string Upload = "Upload";
    public const string OcrProcessed = "OcrProcessed";
    public const string Correction = "Correction";
    public const string ValidationRun = "ValidationRun";
    public const string StatusChanged = "StatusChanged";
    public const string Approval = "Approval";
    public const string Rejection = "Rejection";
    public const string Push = "Push";
    public const string UserRefresh = "UserRefresh";
    public const string ConfigChange = "ConfigChange";
    public const string Login = "Login";
    public const string Logout = "Logout";
}
