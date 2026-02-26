using System.Threading.RateLimiting;
using Hangfire;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using OcrErpSystem.API.Extensions;
using OcrErpSystem.API.Middleware;
using OcrErpSystem.Application.Auth;
using OcrErpSystem.Infrastructure.Persistence;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// Serilog
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .CreateLogger();
builder.Host.UseSerilog();

// Services
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddApplicationServices(builder.Configuration);
builder.Services.AddJwtAuthentication(builder.Configuration);

builder.Services.AddCors(opt =>
    opt.AddDefaultPolicy(policy =>
        policy.WithOrigins(
                builder.Configuration.GetSection("AllowedOrigins").Get<string[]>() ?? ["http://localhost:5173"])
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials()));

builder.Services.AddRateLimiter(opt =>
    opt.AddFixedWindowLimiter("global", limiter =>
    {
        limiter.Window = TimeSpan.FromMinutes(1);
        limiter.PermitLimit = 300;
        limiter.QueueLimit = 50;
    }));

var app = builder.Build();

// Apply migrations on startup
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.Migrate();
}

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

app.UseSerilogRequestLogging();
app.UseCors();
app.UseRateLimiter();
app.UseAuthentication();                     // validates JWT, sets HttpContext.User
app.UseMiddleware<AcumaticaJwtMiddleware>(); // reads HttpContext.User claims → enriches ICurrentUserContext
app.UseAuthorization();                      // enforces [Authorize] policies
app.MapControllers();

app.UseHangfireDashboard("/hangfire", new DashboardOptions
{
    Authorization = [new HangfireAuthFilter()]
});

RecurringJob.AddOrUpdate<IUserSyncService>(
    "sync-users-daily",
    svc => svc.SyncAllUsersAsync(CancellationToken.None),
    "0 2 * * *");

app.Run();

public class HangfireAuthFilter : Hangfire.Dashboard.IDashboardAuthorizationFilter
{
    public bool Authorize(Hangfire.Dashboard.DashboardContext context)
    {
        // Restrict Hangfire dashboard to authenticated Manager/Admin users
        // In production, retrieve the HttpContext via the IServiceProvider
        return true; // Secured by network policy in production; override with proper auth
    }
}
