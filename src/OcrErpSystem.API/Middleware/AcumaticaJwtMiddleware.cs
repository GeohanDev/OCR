using System.Security.Claims;
using OcrErpSystem.Application.Auth;
using OcrErpSystem.Infrastructure.Auth;
using OcrErpSystem.Infrastructure.Persistence.Repositories;

namespace OcrErpSystem.API.Middleware;

/// <summary>
/// Runs after UseAuthentication(). HttpContext.User is already validated by the
/// SmartBearer JWT scheme — this middleware just reads its claims and enriches
/// ICurrentUserContext with the matching local DB user record.
/// </summary>
public class AcumaticaJwtMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<AcumaticaJwtMiddleware> _logger;

    public AcumaticaJwtMiddleware(RequestDelegate next, ILogger<AcumaticaJwtMiddleware> logger)
    {
        _next  = next;
        _logger = logger;
    }

    public async Task InvokeAsync(
        HttpContext   context,
        ICurrentUserContext currentUser,
        IAcumaticaTokenContext tokenContext,
        UserRepository userRepo)
    {
        // Capture the forwarded Acumatica token so AcumaticaClient can use it
        // instead of needing a separate service-account client_credentials token.
        var forwarded = context.Request.Headers["X-Acumatica-Token"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(forwarded))
            tokenContext.ForwardedToken = forwarded;
        if (context.User.Identity?.IsAuthenticated == true
            && currentUser is CurrentUserContext mutableCtx)
        {
            try
            {
                // Claims are already signature-verified by UseAuthentication() above.
                var sub = context.User.FindFirst("sub")?.Value
                       ?? context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

                var username = context.User.FindFirst("preferred_username")?.Value
                            ?? context.User.FindFirst("unique_name")?.Value
                            ?? context.User.FindFirst(ClaimTypes.Name)?.Value
                            ?? sub;

                if (!string.IsNullOrWhiteSpace(username))
                {
                    var user = await userRepo.GetByUsernameAsync(username);
                    if (user is { IsActive: true })
                    {
                        mutableCtx.UserId   = user.Id;
                        mutableCtx.Username = user.Username;
                        mutableCtx.Role     = user.Role.ToString();
                        mutableCtx.BranchId = user.BranchId;

                        // Replace the JWT role claim with the DB role so that
                        // [Authorize(Policy = "...")] always reflects the current
                        // role even after an admin changes it — no re-login required.
                        if (context.User.Identity is ClaimsIdentity identity)
                        {
                            var existingRole = identity.FindFirst(ClaimTypes.Role);
                            if (existingRole is not null) identity.RemoveClaim(existingRole);
                            identity.AddClaim(new Claim(ClaimTypes.Role, user.Role.ToString()));
                        }
                    }
                    else
                    {
                        _logger.LogWarning("Authenticated user '{Username}' not found or inactive in local DB", username);
                    }
                }

                mutableCtx.IpAddress = context.Connection.RemoteIpAddress?.ToString();
                mutableCtx.UserAgent = context.Request.Headers.UserAgent.ToString();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to enrich ICurrentUserContext");
            }
        }

        await _next(context);
    }
}
