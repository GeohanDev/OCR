namespace OcrSystem.Application.Storage;

public interface IFileStorageService
{
    Task<string> StoreAsync(Stream fileStream, string filename, string mimeType, CancellationToken ct = default);
    Task<Stream> ReadAsync(string storagePath, CancellationToken ct = default);
    Task DeleteAsync(string storagePath, CancellationToken ct = default);
    Task<string> GenerateSignedUrlAsync(string storagePath, TimeSpan expiry, CancellationToken ct = default);
    Task<string> ComputeHashAsync(Stream fileStream, CancellationToken ct = default);
}
