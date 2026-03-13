using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OcrSystem.Application.Audit;
using OcrSystem.Application.Auth;
using OcrSystem.Application.DTOs;
using OcrSystem.Domain.Enums;
using OcrSystem.Infrastructure.Persistence.Repositories;

namespace OcrSystem.API.Controllers;

[ApiController]
[Route("api/users")]
[Authorize]
public class UsersController : ControllerBase
{
    private readonly IUserSyncService _syncService;
    private readonly UserRepository _userRepo;
    private readonly IAuditService _audit;
    private readonly ICurrentUserContext _user;

    public UsersController(IUserSyncService syncService, UserRepository userRepo, IAuditService audit, ICurrentUserContext user)
    {
        _syncService = syncService;
        _userRepo = userRepo;
        _audit = audit;
        _user = user;
    }

    [HttpPost("sync")]
    [Authorize(Policy = "ManagerAndAbove")]
    public async Task<IActionResult> Sync(CancellationToken ct)
    {
        var result = await _syncService.SyncAllUsersAsync(ct);
        await _audit.LogAsync("UserRefresh", _user.UserId, null,
            afterValue: result, ipAddress: _user.IpAddress, userAgent: _user.UserAgent, ct: ct);
        return Ok(result);
    }

    [HttpGet]
    [Authorize(Policy = "ManagerAndAbove")]
    public async Task<IActionResult> List([FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default)
    {
        var users = await _userRepo.GetPagedAsync(page, pageSize, ct);
        var total = await _userRepo.CountAsync(ct);
        return Ok(new
        {
            items = users.Select(u => new UserDto(u.Id, u.AcumaticaUserId, u.Username, u.DisplayName,
                u.Email, u.Role.ToString(), u.BranchId, u.Branch?.BranchName,
                u.IsActive, u.LastSyncedAt, u.CreatedAt)),
            totalCount = total, page, pageSize
        });
    }

    [HttpPatch("{id:guid}/role")]
    [Authorize(Policy = "AdminOnly")]
    public async Task<IActionResult> UpdateRole(Guid id, [FromBody] UpdateRoleRequest request, CancellationToken ct)
    {
        if (!Enum.TryParse<UserRole>(request.Role, out var newRole))
            return BadRequest("Invalid role. Valid values: Normal, Manager, Admin");

        var user = await _userRepo.GetByIdAsync(id, ct);
        if (user is null) return NotFound();

        var oldRole = user.Role;
        user.Role = newRole;
        user.UpdatedAt = DateTimeOffset.UtcNow;
        await _userRepo.UpdateAsync(user, ct);

        await _audit.LogAsync("ConfigChange", _user.UserId, null,
            targetEntityType: "User", targetEntityId: id.ToString(),
            beforeValue: new { role = oldRole.ToString() },
            afterValue: new { role = newRole.ToString() },
            ipAddress: _user.IpAddress, userAgent: _user.UserAgent, ct: ct);

        return NoContent();
    }

    [HttpPatch("{id:guid}/active")]
    [Authorize(Policy = "AdminOnly")]
    public async Task<IActionResult> UpdateActive(Guid id, [FromBody] UpdateActiveRequest request, CancellationToken ct)
    {
        var user = await _userRepo.GetByIdAsync(id, ct);
        if (user is null) return NotFound();
        user.IsActive = request.IsActive;
        user.UpdatedAt = DateTimeOffset.UtcNow;
        await _userRepo.UpdateAsync(user, ct);
        return NoContent();
    }
}

public record UpdateRoleRequest(string Role);
public record UpdateActiveRequest(bool IsActive);
