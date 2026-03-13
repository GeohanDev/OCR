using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using System.Text;
using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using OcrSystem.Application.Audit;
using OcrSystem.Application.Auth;
using OcrSystem.Application.Config;
using OcrSystem.Application.Dashboard;
using OcrSystem.Application.Documents;
using OcrSystem.Application.ERP;
using OcrSystem.Application.FieldMapping;
using OcrSystem.Application.OCR;
using OcrSystem.Application.Storage;
using OcrSystem.Application.Trash;
using OcrSystem.Application.Validation;
using OcrSystem.Infrastructure.Auth;
using OcrSystem.Application.Auth;
using OcrSystem.Infrastructure.ERP;
using OcrSystem.Infrastructure.Trash;
using OcrSystem.Infrastructure.Persistence;
using OcrSystem.Infrastructure.Persistence.Repositories;
using OcrSystem.Infrastructure.Services;
using OcrSystem.Infrastructure.Storage;
using OcrSystem.Infrastructure.Validation;
using OcrSystem.Infrastructure.Validation.Validators;
using OcrSystem.OCR;
using OcrSystem.Infrastructure.OCR;

namespace OcrSystem.API.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddApplicationServices(this IServiceCollection services, IConfiguration config)
    {
        // Database
        services.AddDbContext<AppDbContext>(opt =>
            opt.UseNpgsql(config.GetConnectionString("Default"),
                npg => npg.MigrationsAssembly("OcrSystem.Infrastructure")));

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

        // Step 3: PaddleOCR microservice client.
        // Enabled when PaddleOcr:BaseUrl is set (e.g. http://paddleocr:8001).
        // Falls back gracefully to Tesseract when not configured.
        services.AddHttpClient<IPaddleOcrEngine, PaddleOcrEngine>(client =>
        {
            var baseUrl = config["PaddleOcr:BaseUrl"];
            if (!string.IsNullOrWhiteSpace(baseUrl))
                client.BaseAddress = new Uri(baseUrl);
            // PaddleOCR on CPU can take 10-30 s for multi-page scanned documents.
            client.Timeout = TimeSpan.FromMinutes(3);
        });

        // Step 5: Claude full structured extraction.
        // Enabled when Anthropic:ApiKey is set. PaddleOCR produces raw text;
        // Claude extracts ALL configured fields including multi-row table data
        // and per-row checkboxes. Text-only (~$0.002/doc on Haiku 4.5).
        services.AddHttpClient<IClaudeFieldExtractionService, ClaudeFieldExtractionService>(client =>
        {
            client.BaseAddress = new Uri("https://api.anthropic.com/");
            client.Timeout = TimeSpan.FromMinutes(3);
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
