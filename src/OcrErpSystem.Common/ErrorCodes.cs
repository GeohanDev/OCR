namespace OcrErpSystem.Common;

public static class ErrorCodes
{
    public const string NotFound = "NOT_FOUND";
    public const string Unauthorized = "UNAUTHORIZED";
    public const string Forbidden = "FORBIDDEN";
    public const string ValidationFailed = "VALIDATION_FAILED";
    public const string DuplicateDocument = "DUPLICATE_DOCUMENT";
    public const string OcrFailed = "OCR_FAILED";
    public const string ErpLookupFailed = "ERP_LOOKUP_FAILED";
    public const string ApprovalBlocked = "APPROVAL_BLOCKED";
    public const string InvalidStatus = "INVALID_STATUS";
    public const string StorageFailed = "STORAGE_FAILED";
    public const string SyncFailed = "SYNC_FAILED";
}
