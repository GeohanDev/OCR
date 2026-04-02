namespace OcrSystem.Domain.Enums;

public enum DocumentStatus
{
    Uploaded,
    PendingProcess,
    Processing,
    PendingReview,
    ReviewInProgress,
    Approved,
    Rejected,
    Pushed,
    Checked
}
