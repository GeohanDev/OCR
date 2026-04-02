using OcrSystem.Application.CashFlow;

namespace OcrSystem.Infrastructure.Services;

/// <summary>
/// Singleton that tracks real-time progress of the AP aging snapshot capture job.
/// Thread-safe: Kind-0 vendor calls run in parallel (up to 3 concurrent).
/// </summary>
public sealed class CaptureProgressTracker : ICaptureProgressTracker
{
    private volatile CapturePhase _phase = CapturePhase.Idle;
    private int _total;
    private int _completed;
    private DateTimeOffset? _startedAt;
    private string _passLabel = "";
    private int _passTotal;
    private int _passCompleted;

    public void Begin(int totalVendors)
    {
        _total        = totalVendors;
        _completed    = 0;
        _startedAt    = DateTimeOffset.UtcNow;
        _passLabel    = "";
        _passTotal    = 0;
        _passCompleted = 0;
        _phase        = CapturePhase.Preparing;
    }

    public void BeginPass(string passLabel, int passTotal)
    {
        _passLabel    = passLabel;
        _passTotal    = passTotal;
        _passCompleted = 0;
    }

    public void AdvanceVendor()
    {
        Interlocked.Increment(ref _completed);
        Interlocked.Increment(ref _passCompleted);
    }

    public void SetPhase(CapturePhase phase) => _phase = phase;

    public CaptureProgressSnapshot GetSnapshot() =>
        new(_phase, _total, _completed, _passLabel, _passTotal, _passCompleted, _startedAt);
}
