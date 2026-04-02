using Microsoft.Extensions.Caching.Memory;
using OcrSystem.Application.Auth;

namespace OcrSystem.Infrastructure.Auth;

public class AcumaticaSessionService : IAcumaticaSessionService
{
    private readonly IMemoryCache _cache;
    private static readonly TimeSpan _timeout = TimeSpan.FromMinutes(10);

    public AcumaticaSessionService(IMemoryCache cache) => _cache = cache;

    // "started" key has no expiry — persists until EndSession or server restart.
    // Presence indicates the user has had at least one successful keepalive.
    private static string StartedKey(Guid id) => $"acu_started_{id}";

    // "active" key expires after 10 minutes of inactivity (sliding window).
    private static string ActiveKey(Guid id) => $"acu_active_{id}";

    public void StartSession(Guid userId)
    {
        _cache.Set(StartedKey(userId), true); // no expiry — persists until EndSession
        RecordActivity(userId);
    }

    public void RecordActivity(Guid userId)
    {
        _cache.Set(ActiveKey(userId), true,
            new MemoryCacheEntryOptions { SlidingExpiration = _timeout });
    }

    public bool IsTimedOut(Guid userId)
    {
        bool wasStarted = _cache.TryGetValue(StartedKey(userId), out _);
        bool isActive   = _cache.TryGetValue(ActiveKey(userId),   out _);
        // Timed out = was explicitly started but the activity window has since elapsed.
        return wasStarted && !isActive;
    }

    public void EndSession(Guid userId)
    {
        _cache.Remove(StartedKey(userId));
        _cache.Remove(ActiveKey(userId));
    }
}
