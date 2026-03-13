using OcrSystem.Application.Auth;

namespace OcrSystem.Infrastructure.Auth;

public class CurrentUserContext : ICurrentUserContext
{
    public Guid UserId { get; set; }
    public string Username { get; set; } = string.Empty;
    public string Role { get; set; } = "Normal";
    public Guid? BranchId { get; set; }
    public bool IsManagerOrAbove => Role.Equals("Manager", StringComparison.OrdinalIgnoreCase)
                                 || Role.Equals("Admin", StringComparison.OrdinalIgnoreCase);
    public bool IsAdmin => Role.Equals("Admin", StringComparison.OrdinalIgnoreCase);
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }
}
