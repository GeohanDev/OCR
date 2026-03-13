namespace OcrSystem.Application.Auth;

public interface ICurrentUserContext
{
    Guid UserId { get; }
    string Username { get; }
    string Role { get; }
    Guid? BranchId { get; }
    bool IsManagerOrAbove { get; }
    bool IsAdmin { get; }
    string? IpAddress { get; }
    string? UserAgent { get; }
}
