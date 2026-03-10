using System.Threading.RateLimiting;
using Hangfire;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using OcrErpSystem.API.Extensions;
using OcrErpSystem.API.Middleware;
using OcrErpSystem.Application.Auth;
using OcrErpSystem.Application.Storage;
using OcrErpSystem.Application.Trash;
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
    // Idempotent DDL safety net — applied even if the migration file is missing its Designer.cs
    db.Database.ExecuteSqlRaw(
        "ALTER TABLE field_mapping_configs ADD COLUMN IF NOT EXISTS allow_multiple boolean NOT NULL DEFAULT false;");
    db.Database.ExecuteSqlRaw(
        "ALTER TABLE field_mapping_configs ADD COLUMN IF NOT EXISTS dependent_field_key character varying(150);");
    db.Database.ExecuteSqlRaw(
        "ALTER TABLE field_mapping_configs ADD COLUMN IF NOT EXISTS is_manual_entry boolean NOT NULL DEFAULT false;");
    db.Database.ExecuteSqlRaw(
        "ALTER TABLE field_mapping_configs ADD COLUMN IF NOT EXISTS is_checkbox boolean NOT NULL DEFAULT false;");
    // Vendor sync columns — safety net in case the migration didn't run
    db.Database.ExecuteSqlRaw(@"
        DO $$
        BEGIN
            IF NOT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = 'vendors') THEN
                CREATE TABLE vendors (
                    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
                    acumatica_vendor_id VARCHAR(100) NOT NULL,
                    vendor_name VARCHAR(500) NOT NULL,
                    address_line1 VARCHAR(500),
                    address_line2 VARCHAR(500),
                    city VARCHAR(250),
                    state VARCHAR(100),
                    postal_code VARCHAR(50),
                    country VARCHAR(100),
                    payment_terms VARCHAR(100),
                    is_active BOOLEAN NOT NULL DEFAULT TRUE,
                    last_synced_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
                );
                CREATE UNIQUE INDEX IX_vendors_acumatica_vendor_id ON vendors (acumatica_vendor_id);
            END IF;
        END $$;");
    db.Database.ExecuteSqlRaw(
        "ALTER TABLE documents ADD COLUMN IF NOT EXISTS vendor_id UUID REFERENCES vendors(id) ON DELETE SET NULL;");
    db.Database.ExecuteSqlRaw(
        "ALTER TABLE documents ADD COLUMN IF NOT EXISTS vendor_name VARCHAR(500);");
    db.Database.ExecuteSqlRaw(
        "CREATE INDEX IF NOT EXISTS IX_documents_vendor_id ON documents (vendor_id);");

    // Trash columns — safety net in case migration didn't apply
    db.Database.ExecuteSqlRaw(
        "ALTER TABLE documents ADD COLUMN IF NOT EXISTS deleted_at TIMESTAMPTZ;");
    db.Database.ExecuteSqlRaw(
        "ALTER TABLE field_mapping_configs ADD COLUMN IF NOT EXISTS is_deleted boolean NOT NULL DEFAULT false;");
    db.Database.ExecuteSqlRaw(
        "ALTER TABLE field_mapping_configs ADD COLUMN IF NOT EXISTS deleted_at TIMESTAMPTZ;");
    db.Database.ExecuteSqlRaw(
        "ALTER TABLE document_types ADD COLUMN IF NOT EXISTS is_deleted boolean NOT NULL DEFAULT false;");
    db.Database.ExecuteSqlRaw(
        "ALTER TABLE document_types ADD COLUMN IF NOT EXISTS deleted_at TIMESTAMPTZ;");

    // Replace non-partial unique index with partial (active-only) so soft-deleted fields
    // don't block re-creation of the same field name.
    db.Database.ExecuteSqlRaw(@"
        DO $$
        BEGIN
            DROP INDEX IF EXISTS ""IX_field_mapping_configs_document_type_id_field_name"";
            IF NOT EXISTS (
                SELECT 1 FROM pg_indexes
                WHERE tablename = 'field_mapping_configs'
                AND indexname = 'IX_field_mapping_active_name'
            ) THEN
                CREATE UNIQUE INDEX ""IX_field_mapping_active_name""
                    ON field_mapping_configs (document_type_id, field_name)
                    WHERE is_active = true;
            END IF;
        END $$;");

    // Seed Vendor Statement document type and field mappings (idempotent)
    var vsDocTypeId = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";
    db.Database.ExecuteSqlRaw($@"
        INSERT INTO document_types (id, type_key, display_name, plugin_class, is_active, created_at)
        VALUES ('{vsDocTypeId}', 'vendor_statement', 'Vendor Statement',
                'OcrErpSystem.Infrastructure.OCR.OcrPipelineService', true, NOW())
        ON CONFLICT (type_key) DO NOTHING;");

    var vsFields = new (string Id, string FieldName, string Label, string Regex, string Keyword,
                        string ErpKey, string? ErpResp, string? DepKey, bool Required, bool Multi, int Order)[]
    {
        ("11111111-0001-0001-0001-000000000001", "vendorName",         "Vendor Name",         @"(?i)(vendor|supplier)\s*[:\-]?\s*(.+)",    "Vendor",         "VendorName",                            "vendorId",         null,               true,  false, 10),
        ("11111111-0001-0001-0001-000000000002", "statementDate",      "Statement Date",      @"\d{1,2}[\/\-]\d{1,2}[\/\-]\d{2,4}",       "Statement Date", "",                                      null,               null,               true,  false, 20),
        ("11111111-0001-0001-0001-000000000003", "outstandingBalance", "Outstanding Balance", @"[\d,]+\.\d{2}",                            "Outstanding",    "VendorStatement:OutstandingBalance",     null,               null,               true,  false, 30),
        ("11111111-0001-0001-0001-000000000004", "aging_current",      "Current (Not Due)",   @"[\d,]+\.\d{2}",                            "Current",        "VendorStatement:AgingCurrent",          null,               null,               false, false, 40),
        ("11111111-0001-0001-0001-000000000005", "aging_30",           "1-30 Days",           @"[\d,]+\.\d{2}",                            "1-30",           "VendorStatement:Aging30",               null,               null,               false, false, 50),
        ("11111111-0001-0001-0001-000000000006", "aging_60",           "31-60 Days",          @"[\d,]+\.\d{2}",                            "31-60",          "VendorStatement:Aging60",               null,               null,               false, false, 60),
        ("11111111-0001-0001-0001-000000000007", "aging_90plus",       "61+ Days",            @"[\d,]+\.\d{2}",                            "61+",            "VendorStatement:Aging90Plus",           null,               null,               false, false, 70),
        ("11111111-0001-0001-0001-000000000008", "lineInvoiceRef",     "Invoice Ref",         @"[A-Z0-9\-]+",                              "Invoice",        "Bill:VendorRef",                        "RefNbr",           "vendorName",       false, true,  80),
        ("11111111-0001-0001-0001-000000000009", "lineInvoiceDate",    "Invoice Date",        @"\d{1,2}[\/\-]\d{1,2}[\/\-]\d{2,4}",       "Date",           "",                                      null,               null,               false, true,  90),
        ("11111111-0001-0001-0001-000000000010", "lineAmount",         "Invoice Amount",      @"[\d,]+\.\d{2}",                            "Amount",         "Bill:Amount",                           "Amount",           "lineInvoiceRef",   false, true,  100),
        ("11111111-0001-0001-0001-000000000011", "lineBalance",        "Outstanding Balance", @"[\d,]+\.\d{2}",                            "Balance",        "Bill:Balance",                          "Balance",          "lineInvoiceRef",   false, true,  110),
        ("11111111-0001-0001-0001-000000000012", "lineDueDate",        "Due Date",            @"\d{1,2}[\/\-]\d{1,2}[\/\-]\d{2,4}",       "Due",            "",                                      null,               null,               false, true,  120),
    };

    foreach (var f in vsFields)
    {
        var erpResp = f.ErpResp is not null ? $"'{f.ErpResp}'" : "NULL";
        var depKey  = f.DepKey  is not null ? $"'{f.DepKey}'"  : "NULL";
        // Escape single quotes for SQL and double curly-braces so string.Format (called internally
        // by ExecuteSqlRaw) treats them as literal braces rather than format specifiers.
        var regex   = f.Regex.Replace("'", "''").Replace("{", "{{").Replace("}", "}}");
        var sql = $@"
            INSERT INTO field_mapping_configs
                (id, document_type_id, field_name, display_label, regex_pattern, keyword_anchor,
                 erp_mapping_key, erp_response_field, dependent_field_key,
                 is_required, allow_multiple, confidence_threshold, display_order, is_active, created_at, updated_at)
            VALUES
                ('{f.Id}', '{vsDocTypeId}', '{f.FieldName}', '{f.Label.Replace("'", "''")}',
                 '{regex}', '{f.Keyword}',
                 '{f.ErpKey}', {erpResp}, {depKey},
                 {(f.Required ? "true" : "false")}, {(f.Multi ? "true" : "false")},
                 0.70, {f.Order}, true, NOW(), NOW())
            ON CONFLICT DO NOTHING;";
        db.Database.ExecuteSqlRaw(sql);
    }

    // Refresh vendor_name on all documents from their latest extracted vendorName OCR field
    // (uses corrected_value first, then normalized, then raw — picks up manual corrections).
    // TRIM() removes leading/trailing whitespace to avoid duplicate groups in the document list.
    db.Database.ExecuteSqlRaw(@"
        UPDATE documents d
        SET vendor_name = TRIM(COALESCE(ef.corrected_value, ef.normalized_value, ef.raw_value))
        FROM (
            SELECT DISTINCT ON (o.document_id)
                o.document_id,
                ef.corrected_value, ef.normalized_value, ef.raw_value
            FROM ocr_results o
            JOIN extracted_fields ef ON ef.ocr_result_id = o.id
            WHERE LOWER(ef.field_name) = 'vendorname'
              AND TRIM(COALESCE(ef.corrected_value, ef.normalized_value, ef.raw_value)) <> ''
            ORDER BY o.document_id, o.created_at DESC
        ) ef
        WHERE d.id = ef.document_id
          AND (d.vendor_name IS DISTINCT FROM TRIM(COALESCE(ef.corrected_value, ef.normalized_value, ef.raw_value)));");
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

RecurringJob.AddOrUpdate<ITrashPurgeService>(
    "purge-trash-daily",
    svc => svc.PurgeExpiredAsync(CancellationToken.None),
    "0 3 * * *");

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
