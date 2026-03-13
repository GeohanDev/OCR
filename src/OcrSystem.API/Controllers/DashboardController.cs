using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OcrSystem.Application.Audit;
using OcrSystem.Application.Auth;
using OcrSystem.Application.Dashboard;

namespace OcrSystem.API.Controllers;

[ApiController]
[Route("api")]
[Authorize]
public class DashboardController : ControllerBase
{
    private readonly IDashboardService _dashboard;
    private readonly IAuditService _audit;
    private readonly ICurrentUserContext _user;

    public DashboardController(IDashboardService dashboard, IAuditService audit, ICurrentUserContext user)
    {
        _dashboard = dashboard;
        _audit = audit;
        _user = user;
    }

    [HttpGet("dashboard/kpis")]
    public async Task<IActionResult> GetKpis(CancellationToken ct)
    {
        var kpis = await _dashboard.GetKpisAsync(_user.UserId, _user.Role, _user.BranchId, ct);
        return Ok(kpis);
    }

    [HttpGet("audit/logs")]
    [Authorize(Policy = "ManagerAndAbove")]
    public async Task<IActionResult> GetAuditLogs(
        [FromQuery] Guid? documentId,
        [FromQuery] string? eventType,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        var query = new AuditLogQuery(documentId, null, eventType, from, to, page, pageSize);
        var result = await _audit.GetLogsAsync(query, ct);
        return Ok(result);
    }
}
