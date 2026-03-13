using OcrSystem.Application.DTOs;

namespace OcrSystem.Application.Dashboard;

public interface IDashboardService
{
    Task<DashboardKpisDto> GetKpisAsync(Guid requestingUserId, string requestingUserRole, Guid? branchId, CancellationToken ct = default);
}
