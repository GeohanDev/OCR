using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using OcrSystem.Application.Storage;

namespace OcrSystem.Infrastructure.Storage;

public class LocalFileStorageService : IFileStorageService
{
    private readonly string _basePath;
    private readonly string _baseUrl;
    private readonly string _signingKey;

    public LocalFileStorageService(IConfiguration config)
    {
        _basePath = config["Storage:LocalPath"] ?? Path.Combine(Directory.GetCurrentDirectory(), "data", "documents");
        _baseUrl = config["Storage:BaseUrl"] ?? "http://localhost:5000/files";
        _signingKey = config["Storage:SigningKey"] ?? "signed-url-secret";
        Directory.CreateDirectory(_basePath);
    }

    public async Task<string> StoreAsync(Stream fileStream, string filename, string mimeType, CancellationToken ct = default)
    {
        var extension = Path.GetExtension(filename);
        var storageName = $"{Guid.NewGuid():N}{extension}";
        var subdir = storageName[..2];
        var fullDir = Path.Combine(_basePath, subdir);
        Directory.CreateDirectory(fullDir);
        var fullPath = Path.Combine(fullDir, storageName);

        fileStream.Seek(0, SeekOrigin.Begin);
        await using var fs = File.Create(fullPath);
        await fileStream.CopyToAsync(fs, ct);

        return $"{subdir}/{storageName}";
    }

    public Task<Stream> ReadAsync(string storagePath, CancellationToken ct = default)
    {
        var fullPath = Path.Combine(_basePath, storagePath.Replace('/', Path.DirectorySeparatorChar));
        return Task.FromResult<Stream>(File.OpenRead(fullPath));
    }

    public Task DeleteAsync(string storagePath, CancellationToken ct = default)
    {
        var fullPath = Path.Combine(_basePath, storagePath.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(fullPath)) File.Delete(fullPath);
        return Task.CompletedTask;
    }

    public Task<string> GenerateSignedUrlAsync(string storagePath, TimeSpan expiry, CancellationToken ct = default)
    {
        var expiryUnix = DateTimeOffset.UtcNow.Add(expiry).ToUnixTimeSeconds();
        var token = Convert.ToBase64String(
            SHA256.HashData(Encoding.UTF8.GetBytes($"{storagePath}:{expiryUnix}:{_signingKey}")));
        var encodedToken = Uri.EscapeDataString(token);
        return Task.FromResult($"{_baseUrl}/{storagePath}?expires={expiryUnix}&token={encodedToken}");
    }

    public async Task<string> ComputeHashAsync(Stream fileStream, CancellationToken ct = default)
    {
        fileStream.Seek(0, SeekOrigin.Begin);
        var hashBytes = await SHA256.HashDataAsync(fileStream, ct);
        fileStream.Seek(0, SeekOrigin.Begin);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }
}
