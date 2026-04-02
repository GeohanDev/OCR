namespace OcrSystem.Application.CashFlow;

public enum CapturePhase { Idle, Preparing, FetchingVendorAging, FetchingOpenBills, Saving, Done }

public interface ICaptureProgressTracker
{
    void Begin(int totalVendors);
    void BeginPass(string passLabel, int passTotal);
    void AdvanceVendor();
    void SetPhase(CapturePhase phase);
    CaptureProgressSnapshot GetSnapshot();
}

public sealed record CaptureProgressSnapshot(
    CapturePhase Phase,
    int TotalVendors,
    int CompletedVendors,
    string PassLabel,
    int PassTotal,
    int PassCompleted,
    DateTimeOffset? StartedAt);
