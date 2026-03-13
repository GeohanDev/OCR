using OcrSystem.Application.Dashboard;
using OcrSystem.Application.Documents;
using OcrSystem.Application.DTOs;
using OcrSystem.Domain.Enums;
using OcrSystem.Infrastructure.Persistence.Repositories;

namespace OcrSystem.Infrastructure.Services;

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
        int rejected = counts[DocumentStatus.Rejected];
        int checked_ = counts[DocumentStatus.Checked];

        var recentQuery = new DocumentListQuery(requestingUserId, requestingUserRole, branchId, null, null, null, null, 1, 10);
        var recent = await _docs.ListAsync(recentQuery, ct);

        return new DashboardKpisDto(
            total, pendingReview, rejected, checked_,
            recent.Items.Select(d => new RecentDocumentDto(
                d.Id, d.OriginalFilename, d.Status.ToString(),
                d.UploadedAt, d.UploadedByUser?.Username ?? "")).ToList());
    }
}
