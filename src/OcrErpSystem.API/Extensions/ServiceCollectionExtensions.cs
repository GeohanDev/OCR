using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using OcrErpSystem.Application.Audit;
using OcrErpSystem.Application.Auth;
using OcrErpSystem.Application.Config;
using OcrErpSystem.Application.Dashboard;
using OcrErpSystem.Application.Documents;
using OcrErpSystem.Application.ERP;
using OcrErpSystem.Application.FieldMapping;
using OcrErpSystem.Application.OCR;
using OcrErpSystem.Application.Storage;
using OcrErpSystem.Application.Validation;
using OcrErpSystem.Infrastructure.Auth;
using OcrErpSystem.Infrastructure.ERP;
using OcrErpSystem.Infrastructure.Persistence;
using OcrErpSystem.Infrastructure.Persistence.Repositories;
using OcrErpSystem.Infrastructure.Services;
using OcrErpSystem.Infrastructure.Storage;
using OcrErpSystem.Infrastructure.Validation;
using OcrErpSystem.Infrastructure.Validation.Validators;
using OcrErpSystem.OCR;
using OcrErpSystem.Infrastructure.OCR;

namespace OcrErpSystem.API.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddApplicationServices(this IServiceCollection services, IConfiguration config)
    {
        // Database
        services.AddDbContext<AppDbContext>(opt =>
            opt.UseNpgsql(config.GetConnectionString("Default"),
                npg => npg.MigrationsAssembly("OcrErpSystem.Infrastructure")));

        // Repositories
        services.AddScoped<DocumentRepository>();
        services.AddScoped<UserRepository>();
        services.AddScoped<OcrResultRepository>();
        services.AddScoped<ValidationRepository>();
        services.AddScoped<AuditRepository>();
        services.AddScoped<FieldMappingRepository>();
        services.AddScoped<BranchRepository>();
        services.AddScoped<SystemConfigRepository>();

        // Application Services
        services.AddScoped<IDocumentService, DocumentService>();
        services.AddScoped<IFieldMappingService, FieldMappingService>();
        services.AddScoped<IAuditService, AuditService>();
        services.AddScoped<ISystemConfigService, SystemConfigService>();
        services.AddScoped<IDashboardService, DashboardService>();
        services.AddScoped<IUserSyncService, UserSyncService>();

        // Storage
        services.AddScoped<IFileStorageService, LocalFileStorageService>();

        // ERP
        services.AddHttpClient<IErpIntegrationService, AcumaticaClient>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
        });

        // Auth context
        services.AddScoped<ICurrentUserContext, CurrentUserContext>();

        // Validation
        services.AddScoped<IValidationService, ValidationService>();
        services.AddScoped<IFieldValidator, RequiredFieldValidator>();
        services.AddScoped<IFieldValidator, ErpVendorValidator>();
        services.AddScoped<IFieldValidator, ErpCurrencyValidator>();
        services.AddScoped<IFieldValidator, ErpBranchValidator>();
        services.AddScoped<IFieldValidator, FormatValidator>();

        // OCR Pipeline
        services.AddScoped<IImagePreprocessor, ImagePreprocessor>();
        services.AddScoped<ITesseractOcrEngine, TesseractOcrEngine>();
        services.AddScoped<IFieldExtractor, FieldExtractor>();
        services.AddScoped<IFieldNormalizer, FieldNormalizer>();
        services.AddScoped<IConfidenceScorer, ConfidenceScorer>();
        services.AddScoped<IOcrService, Infrastructure.OCR.OcrPipelineService>();

        // Memory cache for ERP lookups
        services.AddMemoryCache();

        // Hangfire
        services.AddHangfire(cfg => cfg.UsePostgreSqlStorage(config.GetConnectionString("Default")));
        services.AddHangfireServer();

        return services;
    }

    public static IServiceCollection AddJwtAuthentication(this IServiceCollection services, IConfiguration config)
    {
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.Authority = config["Acumatica:Authority"];
                options.Audience = config["Acumatica:Audience"];
                options.TokenValidationParameters = new Microsoft.IdentityModel.Tokens.TokenValidationParameters
                {
                    ValidateIssuerSigningKey = true,
                    ValidateIssuer = !string.IsNullOrWhiteSpace(config["Acumatica:Authority"]),
                    ValidateAudience = !string.IsNullOrWhiteSpace(config["Acumatica:Audience"]),
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromMinutes(5)
                };
                options.Events = new JwtBearerEvents
                {
                    OnMessageReceived = ctx =>
                    {
                        if (ctx.Request.Cookies.ContainsKey("jwt"))
                            ctx.Token = ctx.Request.Cookies["jwt"];
                        return Task.CompletedTask;
                    }
                };
            });

        services.AddAuthorization(opt =>
        {
            opt.AddPolicy("ManagerAndAbove", p => p.RequireRole("Manager", "Admin"));
            opt.AddPolicy("AdminOnly", p => p.RequireRole("Admin"));
        });

        return services;
    }
}
