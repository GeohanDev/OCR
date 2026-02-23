using System.Text.Json;
using OcrErpSystem.Application.Config;
using OcrErpSystem.Domain.Entities;
using OcrErpSystem.Infrastructure.Persistence.Repositories;

namespace OcrErpSystem.Infrastructure.Services;

public class SystemConfigService : ISystemConfigService
{
    private readonly SystemConfigRepository _repo;

    public SystemConfigService(SystemConfigRepository repo) => _repo = repo;

    public Task<string?> GetAsync(string key, CancellationToken ct = default) =>
        _repo.GetValueAsync(key, ct);

    public async Task<T?> GetAsync<T>(string key, CancellationToken ct = default)
    {
        var value = await _repo.GetValueAsync(key, ct);
        if (value is null) return default;
        try { return JsonSerializer.Deserialize<T>(value); }
        catch { return default; }
    }

    public Task SetAsync(string key, string value, Guid updatedBy, string? description = null, bool isSensitive = false, CancellationToken ct = default) =>
        _repo.UpsertAsync(new SystemConfig
        {
            Key = key, Value = value, Description = description,
            IsSensitive = isSensitive, UpdatedBy = updatedBy, UpdatedAt = DateTimeOffset.UtcNow
        }, ct);

    public async Task<IReadOnlyDictionary<string, string>> GetAllAsync(bool includeSensitive = false, CancellationToken ct = default) =>
        await _repo.GetAllAsync(includeSensitive, ct);
}
