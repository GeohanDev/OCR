using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.IdentityModel.Tokens;
using OcrErpSystem.Application.Auth;
using OcrErpSystem.Infrastructure.Auth;
using OcrErpSystem.Infrastructure.Persistence.Repositories;

namespace OcrErpSystem.API.Middleware;

public class AcumaticaJwtMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<AcumaticaJwtMiddleware> _logger;

    public AcumaticaJwtMiddleware(RequestDelegate next, ILogger<AcumaticaJwtMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, ICurrentUserContext currentUser, UserRepository userRepo)
    {
        var token = ExtractToken(context);
        if (token is not null && currentUser is CurrentUserContext mutableCtx)
        {
            try
            {
                var handler = new JwtSecurityTokenHandler();
                var jwt = handler.ReadJwtToken(token);

                var sub = jwt.Claims.FirstOrDefault(c => c.Type == "sub" || c.Type == ClaimTypes.NameIdentifier)?.Value;
                var username = jwt.Claims.FirstOrDefault(c => c.Type == "preferred_username" || c.Type == "username" || c.Type == ClaimTypes.Name)?.Value;

                if (!string.IsNullOrWhiteSpace(username))
                {
                    var user = await userRepo.GetByUsernameAsync(username);
                    if (user is not null && user.IsActive)
                    {
                        mutableCtx.UserId = user.Id;
                        mutableCtx.Username = user.Username;
                        mutableCtx.Role = user.Role.ToString();
                        mutableCtx.BranchId = user.BranchId;
                    }
                }

                mutableCtx.IpAddress = context.Connection.RemoteIpAddress?.ToString();
                mutableCtx.UserAgent = context.Request.Headers.UserAgent.ToString();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "JWT parsing failed — continuing unauthenticated");
            }
        }

        await _next(context);
    }

    private static string? ExtractToken(HttpContext context)
    {
        var authHeader = context.Request.Headers.Authorization.ToString();
        if (authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return authHeader["Bearer ".Length..].Trim();
        return context.Request.Cookies["jwt"];
    }
}
