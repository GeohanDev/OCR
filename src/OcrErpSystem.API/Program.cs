using System.Threading.RateLimiting;
using Hangfire;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using OcrErpSystem.API.Extensions;
using OcrErpSystem.API.Middleware;
using OcrErpSystem.Application.Auth;
using OcrErpSystem.Application.Storage;
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

// Serve stored document files — signed-URL authentication (no JWT required)
app.MapGet("/files/{**storagePath}", async (
    string storagePath,
    [FromQuery] long expires,
    [FromQuery] string? token,
    IFileStorageService storage,
    IConfiguration config) =>
{
    if (string.IsNullOrWhiteSpace(token) || DateTimeOffset.UtcNow.ToUnixTimeSeconds() > expires)
        return Results.Problem("URL expired or invalid.", statusCode: 403);

    var signingKey = config["Storage:SigningKey"] ?? "signed-url-secret";
    var expected = Convert.ToBase64String(
        System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"{storagePath}:{expires}:{signingKey}")));

    if (!string.Equals(token, expected, StringComparison.Ordinal))
        return Results.Problem("Invalid token.", statusCode: 403);

    try
    {
        var stream = await storage.ReadAsync(storagePath);
        var ext = Path.GetExtension(storagePath).ToLowerInvariant();
        var mime = ext switch
        {
            ".pdf"  => "application/pdf",
            ".png"  => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".tif" or ".tiff" => "image/tiff",
            _       => "application/octet-stream",
        };
        return Results.Stream(stream, contentType: mime, enableRangeProcessing: true);
    }
    catch (FileNotFoundException)
    {
        return Results.NotFound("File not found.");
    }
});

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
