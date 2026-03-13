namespace OcrSystem.Domain.Enums;

public enum DocumentStatus
{
    Uploaded,
    Processing,
    PendingReview,
    ReviewInProgress,
    Approved,
    Rejected,
    Pushed,
    Checked
}
