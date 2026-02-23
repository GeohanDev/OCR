using OcrErpSystem.Application.Dashboard;
using OcrErpSystem.Application.Documents;
using OcrErpSystem.Application.DTOs;
using OcrErpSystem.Domain.Enums;
using OcrErpSystem.Infrastructure.Persistence.Repositories;

namespace OcrErpSystem.Infrastructure.Services;

public class DashboardService : IDashboardService
{
    private readonly DocumentRepository _docs;

    public DashboardService(DocumentRepository docs) => _docs = docs;

    public async Task<DashboardKpisDto> GetKpisAsync(Guid requestingUserId, string requestingUserRole, Guid? branchId, CancellationToken ct = default)
    {
        bool isNormal = requestingUserRole.Equals("Normal", StringComparison.OrdinalIgnoreCase);
        bool isManager = requestingUserRole.Equals("Manager", StringComparison.OrdinalIgnoreCase);
        Guid? filterUserId = isNormal ? requestingUserId : null;
        Guid? filterBranchId = isManager ? branchId : null;

        var statuses = Enum.GetValues<DocumentStatus>();
        var counts = new Dictionary<DocumentStatus, int>();
        foreach (var s in statuses)
            counts[s] = await _docs.CountByStatusAsync(s, filterBranchId, filterUserId, ct);

        int total = counts.Values.Sum();
        int pendingReview = counts[DocumentStatus.PendingReview] + counts[DocumentStatus.ReviewInProgress];
        int approved = counts[DocumentStatus.Approved];
        int rejected = counts[DocumentStatus.Rejected];
        int pushed = counts[DocumentStatus.Pushed];

        var recentQuery = new DocumentListQuery(requestingUserId, requestingUserRole, branchId, null, null, null, null, 1, 10);
        var recent = await _docs.ListAsync(recentQuery, ct);

        return new DashboardKpisDto(
            total, pendingReview, approved, rejected, pushed,
            recent.Items.Select(d => new RecentDocumentDto(
                d.Id, d.OriginalFilename, d.Status.ToString(),
                d.UploadedAt, d.UploadedByUser?.Username ?? "")).ToList());
    }
}
