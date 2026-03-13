using Microsoft.Extensions.Logging;
using OcrSystem.Application.Auth;
using OcrSystem.Application.ERP;
using OcrSystem.Domain.Entities;
using OcrSystem.Domain.Enums;
using OcrSystem.Infrastructure.Persistence.Repositories;

namespace OcrSystem.Infrastructure.Auth;

public class UserSyncService : IUserSyncService
{
    private readonly IErpIntegrationService _erp;
    private readonly UserRepository _users;
    private readonly BranchRepository _branches;
    private readonly ILogger<UserSyncService> _logger;

    private static readonly Dictionary<string, UserRole> RoleMapping = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Administrator"] = UserRole.Admin,
        ["Manager"] = UserRole.Manager,
        ["SalesManager"] = UserRole.Manager,
        ["FinanceManager"] = UserRole.Manager,
    };

    public UserSyncService(IErpIntegrationService erp, UserRepository users, BranchRepository branches, ILogger<UserSyncService> logger)
    {
        _erp = erp;
        _users = users;
        _branches = branches;
        _logger = logger;
    }

    public async Task<UserSyncResult> SyncAllUsersAsync(CancellationToken ct = default)
    {
        int created = 0, updated = 0, deactivated = 0;
        try
        {
            var erpBranches = await _erp.FetchAllBranchesAsync(ct);
            foreach (var eb in erpBranches)
                await _branches.UpsertAsync(new Branch
                {
                    AcumaticaBranchId = eb.BranchId,
                    BranchCode = eb.BranchCode,
                    BranchName = eb.BranchName,
                    IsActive = eb.IsActive,
                    SyncedAt = DateTimeOffset.UtcNow
                }, ct);

            var erpUsers = await _erp.FetchAllUsersAsync(ct);
            var syncedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var eu in erpUsers)
            {
                syncedIds.Add(eu.UserId);
                var existing = await _users.GetByAcumaticaIdAsync(eu.UserId, ct);
                var role = MapRole(eu.Roles);
                Branch? branch = null;
                if (!string.IsNullOrWhiteSpace(eu.BranchCode))
                    branch = await _branches.GetByAcumaticaIdAsync(eu.BranchCode, ct);

                if (existing is null)
                {
                    await _users.UpsertAsync(new User
                    {
                        AcumaticaUserId = eu.UserId,
                        Username = eu.Username,
                        DisplayName = eu.FullName,
                        Email = eu.Email,
                        Role = role,
                        BranchId = branch?.Id,
                        IsActive = true,
                        LastSyncedAt = DateTimeOffset.UtcNow
                    }, ct);
                    created++;
                }
                else
                {
                    existing.Username = eu.Username;
                    existing.DisplayName = eu.FullName;
                    existing.Email = eu.Email;
                    existing.Role = role;
                    existing.BranchId = branch?.Id;
                    existing.IsActive = true;
                    existing.LastSyncedAt = DateTimeOffset.UtcNow;
                    existing.UpdatedAt = DateTimeOffset.UtcNow;
                    await _users.UpdateAsync(existing, ct);
                    updated++;
                }
            }

            var allLocal = await _users.GetAllAsync(ct);
            foreach (var u in allLocal.Where(u => u.IsActive && !syncedIds.Contains(u.AcumaticaUserId)))
            {
                u.IsActive = false;
                u.UpdatedAt = DateTimeOffset.UtcNow;
                await _users.UpdateAsync(u, ct);
                deactivated++;
            }

            _logger.LogInformation("User sync: created={C} updated={U} deactivated={D}", created, updated, deactivated);
            return new UserSyncResult(created, updated, deactivated);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "User sync failed");
            return new UserSyncResult(created, updated, deactivated, ex.Message);
        }
    }

    public async Task SyncUserAsync(string acumaticaUserId, CancellationToken ct = default)
    {
        var allUsers = await _erp.FetchAllUsersAsync(ct);
        var eu = allUsers.FirstOrDefault(u => u.UserId.Equals(acumaticaUserId, StringComparison.OrdinalIgnoreCase));
        if (eu is null) return;

        var role = MapRole(eu.Roles);
        Branch? branch = null;
        if (!string.IsNullOrWhiteSpace(eu.BranchCode))
            branch = await _branches.GetByAcumaticaIdAsync(eu.BranchCode, ct);

        await _users.UpsertAsync(new User
        {
            AcumaticaUserId = eu.UserId,
            Username = eu.Username,
            DisplayName = eu.FullName,
            Email = eu.Email,
            Role = role,
            BranchId = branch?.Id,
            IsActive = true,
            LastSyncedAt = DateTimeOffset.UtcNow
        }, ct);
    }

    private static UserRole MapRole(IReadOnlyList<string> erpRoles)
    {
        foreach (var role in erpRoles)
            if (RoleMapping.TryGetValue(role, out var mapped))
                return mapped;
        return UserRole.Normal;
    }
}
