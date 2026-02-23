using OcrErpSystem.Application.DTOs;

namespace OcrErpSystem.Application.Dashboard;

public interface IDashboardService
{
    Task<DashboardKpisDto> GetKpisAsync(Guid requestingUserId, string requestingUserRole, Guid? branchId, CancellationToken ct = default);
}
