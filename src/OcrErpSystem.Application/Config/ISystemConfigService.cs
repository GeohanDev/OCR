namespace OcrErpSystem.Application.Config;

public interface ISystemConfigService
{
    Task<string?> GetAsync(string key, CancellationToken ct = default);
    Task<T?> GetAsync<T>(string key, CancellationToken ct = default);
    Task SetAsync(string key, string value, Guid updatedBy, string? description = null, bool isSensitive = false, CancellationToken ct = default);
    Task<IReadOnlyDictionary<string, string>> GetAllAsync(bool includeSensitive = false, CancellationToken ct = default);
}
