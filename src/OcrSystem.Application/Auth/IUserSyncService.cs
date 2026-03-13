namespace OcrSystem.Application.Auth;

public interface IUserSyncService
{
    Task<UserSyncResult> SyncAllUsersAsync(CancellationToken ct = default);
    Task SyncUserAsync(string acumaticaUserId, CancellationToken ct = default);
}

public record UserSyncResult(int Created, int Updated, int Deactivated, string? ErrorMessage = null);
