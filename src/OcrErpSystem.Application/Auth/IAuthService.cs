using OcrErpSystem.Application.DTOs;

namespace OcrErpSystem.Application.Auth;

public interface IAuthService
{
    Task<AuthResult> ExchangeCodeAsync(string code, string redirectUri, CancellationToken ct = default);
    Task<AuthResult> RefreshTokenAsync(string refreshToken, CancellationToken ct = default);
    Task RevokeTokenAsync(string refreshToken, CancellationToken ct = default);
    Task<UserDto?> GetCurrentUserAsync(Guid userId, CancellationToken ct = default);
}

public record AuthResult(
    string AccessToken,
    string RefreshToken,
    DateTimeOffset ExpiresAt,
    UserDto User);
