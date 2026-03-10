using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using System.Text;
using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using OcrErpSystem.Application.Audit;
using OcrErpSystem.Application.Auth;
using OcrErpSystem.Application.Config;
using OcrErpSystem.Application.Dashboard;
using OcrErpSystem.Application.Documents;
using OcrErpSystem.Application.ERP;
using OcrErpSystem.Application.FieldMapping;
using OcrErpSystem.Application.OCR;
using OcrErpSystem.Application.Storage;
using OcrErpSystem.Application.Trash;
using OcrErpSystem.Application.Validation;
using OcrErpSystem.Infrastructure.Auth;
using OcrErpSystem.Application.Auth;
using OcrErpSystem.Infrastructure.ERP;
using OcrErpSystem.Infrastructure.Trash;
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
        services.AddScoped<VendorRepository>();

        // Application Services
        services.AddScoped<IDocumentService, DocumentService>();
        services.AddScoped<IFieldMappingService, FieldMappingService>();
        services.AddScoped<IAuditService, AuditService>();
        services.AddScoped<ISystemConfigService, SystemConfigService>();
        services.AddScoped<IDashboardService, DashboardService>();
        services.AddScoped<IUserSyncService, UserSyncService>();
        services.AddScoped<IVendorSyncService, VendorSyncService>();
        services.AddScoped<ITrashPurgeService, TrashPurgeService>();

        // Storage
        services.AddScoped<IFileStorageService, LocalFileStorageService>();

        // ERP
        services.AddScoped<IAcumaticaTokenContext, AcumaticaTokenContext>();
        services.AddHttpClient<IErpIntegrationService, AcumaticaClient>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(90);
        });

        // Auth context
        services.AddScoped<ICurrentUserContext, CurrentUserContext>();

        // Validation
        services.AddScoped<IValidationService, ValidationService>();
        services.AddSingleton<IOwnCompanyService, OwnCompanyService>();
        services.AddScoped<IVendorResolutionContext, VendorResolutionContext>();
        services.AddScoped<IValidationFieldContext, ValidationFieldContext>();
        services.AddScoped<IFieldValidator, RequiredFieldValidator>();
        services.AddScoped<IFieldValidator, ErpVendorValidator>();
        services.AddScoped<IFieldValidator, ErpVendorNameValidator>();
        services.AddScoped<IFieldValidator, ErpCurrencyValidator>();
        services.AddScoped<IFieldValidator, ErpBranchValidator>();
        services.AddScoped<IFieldValidator, ErpApInvoiceValidator>();
        services.AddScoped<IFieldValidator, DynamicErpValidator>();
        services.AddScoped<IFieldValidator, ErpVendorStatementValidator>();
        services.AddScoped<IFieldValidator, FormatValidator>();

        // OCR Pipeline
        services.AddScoped<IImagePreprocessor, ImagePreprocessor>();
        services.AddScoped<ITesseractOcrEngine, TesseractOcrEngine>();
        services.AddScoped<IFieldExtractor, FieldExtractor>();
        services.AddScoped<IFieldNormalizer, FieldNormalizer>();
        services.AddScoped<IConfidenceScorer, ConfidenceScorer>();
        services.AddScoped<IOcrService, Infrastructure.OCR.OcrPipelineService>();

        // Claude OCR engine — enabled when Anthropic:ApiKey is set in configuration.
        // Falls back gracefully to Tesseract when the key is absent.
        services.AddHttpClient<IClaudeOcrEngine, ClaudeOcrEngine>(client =>
        {
            client.BaseAddress = new Uri("https://api.anthropic.com/");
            // 8 minutes — Claude Sonnet on large multi-page scanned docs can take 3-5 min.
            // Keep well above the escalation path (primary + fallback model both running).
            client.Timeout     = TimeSpan.FromMinutes(8);
        });

        // Memory cache for ERP lookups
        services.AddMemoryCache();

        // Hangfire
        services.AddHangfire(cfg => cfg.UsePostgreSqlStorage(config.GetConnectionString("Default")));
        services.AddHangfireServer();

        return services;
    }

    public static IServiceCollection AddJwtAuthentication(this IServiceCollection services, IConfiguration config)
    {
        var authority = config["Acumatica:Authority"] ?? string.Empty;
        var rsaN      = config["Acumatica:SigningKey:N"] ?? string.Empty;
        var rsaE      = config["Acumatica:SigningKey:E"] ?? string.Empty;
        var demoKey   = config["Demo:SigningKey"] ?? string.Empty;

        // ── Routing: inspect the alg header so each token reaches its correct scheme ──
        // RS256  → "Acumatica" scheme  (Acumatica Identity Server, public-key validation)
        // HS256  → "Demo"      scheme  (demo-login tokens, symmetric key)
        services.AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme = "SmartBearer";
            options.DefaultChallengeScheme    = "SmartBearer";
        })
        .AddPolicyScheme("SmartBearer", "Acumatica or Demo JWT", options =>
        {
            options.ForwardDefaultSelector = ctx =>
            {
                var header = ctx.Request.Headers.Authorization.FirstOrDefault() ?? string.Empty;
                if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var handler = new JwtSecurityTokenHandler();
                        var raw     = header["Bearer ".Length..].Trim();
                        if (handler.CanReadToken(raw))
                        {
                            var alg = handler.ReadJwtToken(raw).Header.Alg;
                            if (string.Equals(alg, SecurityAlgorithms.HmacSha256,
                                    StringComparison.OrdinalIgnoreCase))
                                return "Demo";
                        }
                    }
                    catch { /* fall through to Acumatica */ }
                }
                return "Acumatica";
            };
        })

        // ── Acumatica scheme: RS256 tokens from Acumatica Identity Server ─────────────
        // Primary path  → Authority is set, OIDC discovery fetches the JWKS automatically.
        // Fallback path → Authority is empty, use the hardcoded RSA public key (N + E).
        .AddJwtBearer("Acumatica", options =>
        {
            if (!string.IsNullOrWhiteSpace(authority))
            {
                // Automatic JWKS discovery — handles key rotation without code changes.
                // Setting Authority causes the middleware to fetch the OIDC discovery document
                // and populate ValidIssuer + IssuerSigningKeys automatically; do NOT set
                // ValidIssuer manually here or it may conflict with the discovered value.
                options.Authority            = authority;
                options.RequireHttpsMetadata = authority.StartsWith("https://",
                                                   StringComparison.OrdinalIgnoreCase);
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuerSigningKey = true,
                    ValidateIssuer           = true,   // issuer read from discovery document
                    ValidateAudience         = false,
                    ValidateLifetime         = true,
                    ClockSkew                = TimeSpan.FromMinutes(5),
                };
            }
            else if (!string.IsNullOrWhiteSpace(rsaN) && !string.IsNullOrWhiteSpace(rsaE))
            {
                // Fallback: hardcoded RSA public key (e.g. for offline dev).
                var rsa = RSA.Create();
                rsa.ImportParameters(new RSAParameters
                {
                    Modulus  = Base64UrlEncoder.DecodeBytes(rsaN),
                    Exponent = Base64UrlEncoder.DecodeBytes(rsaE),
                });
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuerSigningKey = true,
                    ValidateIssuer           = false,
                    ValidateAudience         = false,
                    ValidateLifetime         = true,
                    ClockSkew                = TimeSpan.FromMinutes(5),
                    IssuerSigningKey         = new RsaSecurityKey(rsa),
                };
            }

            options.Events = new JwtBearerEvents
            {
                OnMessageReceived = ctx =>
                {
                    if (ctx.Request.Cookies.ContainsKey("jwt"))
                        ctx.Token = ctx.Request.Cookies["jwt"];
                    return Task.CompletedTask;
                },
            };
        })

        // ── Demo scheme: HS256 tokens issued by DemoLogin ────────────────────────────
        .AddJwtBearer("Demo", options =>
        {
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuerSigningKey = !string.IsNullOrWhiteSpace(demoKey),
                ValidateIssuer           = false,
                ValidateAudience         = false,
                ValidateLifetime         = true,
                ClockSkew                = TimeSpan.FromMinutes(5),
                IssuerSigningKey         = !string.IsNullOrWhiteSpace(demoKey)
                    ? new SymmetricSecurityKey(Encoding.UTF8.GetBytes(demoKey))
                    : null,
            };
            options.Events = new JwtBearerEvents
            {
                OnMessageReceived = ctx =>
                {
                    if (ctx.Request.Cookies.ContainsKey("jwt"))
                        ctx.Token = ctx.Request.Cookies["jwt"];
                    return Task.CompletedTask;
                },
            };
        });

        services.AddAuthorization(opt =>
        {
            opt.AddPolicy("ManagerAndAbove", p => p.RequireRole("Manager", "Admin"));
            opt.AddPolicy("AdminOnly",        p => p.RequireRole("Admin"));
        });

        return services;
    }
}
