using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OcrErpSystem.Infrastructure.Persistence.Migrations
{
    [Migration("20260309000002_SeedVendorStatementFields")]
    public partial class SeedVendorStatementFields : Migration
    {
        // Fixed GUIDs so re-running is idempotent
        private static readonly Guid DocTypeId = new("a1b2c3d4-e5f6-7890-abcd-ef1234567890");

        private static readonly (Guid Id, string FieldName, string Label, string? Regex, string? Keyword, string ErpKey, string? ErpResponseField, string? DependentFieldKey, bool Required, bool AllowMultiple, int Order)[] Fields =
        [
            (new("11111111-0001-0001-0001-000000000001"), "vendorName",           "Vendor Name",           @"(?i)(vendor|supplier)\s*[:\-]?\s*(.+)",                    "Vendor",          "VendorName",                               "vendorId",       null,          true,  false, 10),
            (new("11111111-0001-0001-0001-000000000002"), "statementDate",        "Statement Date",        @"\d{1,2}[\/\-]\d{1,2}[\/\-]\d{2,4}",                       "Statement Date",  "",                                         null,             null,          true,  false, 20),
            (new("11111111-0001-0001-0001-000000000003"), "outstandingBalance",   "Outstanding Balance",   @"[\d,]+\.\d{2}",                                            "Outstanding",     "VendorStatement:OutstandingBalance",        null,             null,          true,  false, 30),
            (new("11111111-0001-0001-0001-000000000004"), "aging_current",        "Current (Not Due)",     @"[\d,]+\.\d{2}",                                            "Current",         "VendorStatement:AgingCurrent",             null,             null,          false, false, 40),
            (new("11111111-0001-0001-0001-000000000005"), "aging_30",             "1–30 Days",             @"[\d,]+\.\d{2}",                                            "1-30",            "VendorStatement:Aging30",                  null,             null,          false, false, 50),
            (new("11111111-0001-0001-0001-000000000006"), "aging_60",             "31–60 Days",            @"[\d,]+\.\d{2}",                                            "31-60",           "VendorStatement:Aging60",                  null,             null,          false, false, 60),
            (new("11111111-0001-0001-0001-000000000007"), "aging_90plus",         "61+ Days",              @"[\d,]+\.\d{2}",                                            "61+",             "VendorStatement:Aging90Plus",              null,             null,          false, false, 70),
            // Line-item columns (AllowMultiple = true)
            (new("11111111-0001-0001-0001-000000000008"), "lineInvoiceRef",       "Invoice Ref",           @"[A-Z0-9\-]+",                                              "Invoice",         "Bill:VendorRef",                           "RefNbr",         "vendorName",  false, true,  80),
            (new("11111111-0001-0001-0001-000000000009"), "lineInvoiceDate",      "Invoice Date",          @"\d{1,2}[\/\-]\d{1,2}[\/\-]\d{2,4}",                       "Date",            "",                                         null,             null,          false, true,  90),
            (new("11111111-0001-0001-0001-000000000010"), "lineAmount",           "Invoice Amount",        @"[\d,]+\.\d{2}",                                            "Amount",          "Bill:Amount",                              "Amount",         "lineInvoiceRef", false, true, 100),
            (new("11111111-0001-0001-0001-000000000011"), "lineBalance",          "Outstanding Balance",   @"[\d,]+\.\d{2}",                                            "Balance",         "Bill:Balance",                             "Balance",        "lineInvoiceRef", false, true, 110),
            (new("11111111-0001-0001-0001-000000000012"), "lineDueDate",          "Due Date",              @"\d{1,2}[\/\-]\d{1,2}[\/\-]\d{2,4}",                       "Due",             "",                                         null,             null,          false, true,  120),
        ];

        protected override void Up(MigrationBuilder migrationBuilder)
        {
            var now = DateTimeOffset.UtcNow;

            // Insert document type (idempotent)
            migrationBuilder.Sql($@"
                INSERT INTO document_types (id, type_key, display_name, plugin_class, is_active, created_at)
                VALUES ('{DocTypeId}', 'vendor_statement', 'Vendor Statement', 'OcrErpSystem.Infrastructure.OCR.OcrPipelineService', true, '{now:o}')
                ON CONFLICT (type_key) DO NOTHING;
            ");

            // Insert field mappings (idempotent)
            foreach (var f in Fields)
            {
                var regex   = f.Regex?.Replace("'", "''") ?? "";
                var keyword = f.Keyword?.Replace("'", "''") ?? "";
                var erpKey  = f.ErpKey.Replace("'", "''");
                var erpResp = f.ErpResponseField is not null ? $"'{f.ErpResponseField}'" : "NULL";
                var depKey  = f.DependentFieldKey is not null ? $"'{f.DependentFieldKey}'" : "NULL";
                var label   = f.Label.Replace("'", "''");

                migrationBuilder.Sql($@"
                    INSERT INTO field_mapping_configs
                        (id, document_type_id, field_name, display_label, regex_pattern, keyword_anchor,
                         erp_mapping_key, erp_response_field, dependent_field_key,
                         is_required, allow_multiple, confidence_threshold, display_order, is_active, created_at, updated_at)
                    VALUES
                        ('{f.Id}', '{DocTypeId}', '{f.FieldName}', '{label}', '{regex}', '{keyword}',
                         '{erpKey}', {erpResp}, {depKey},
                         {(f.Required ? "true" : "false")}, {(f.AllowMultiple ? "true" : "false")},
                         0.70, {f.Order}, true, '{now:o}', '{now:o}')
                    ON CONFLICT DO NOTHING;
                ");
            }
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql($"DELETE FROM field_mapping_configs WHERE document_type_id = '{DocTypeId}';");
            migrationBuilder.Sql($"DELETE FROM document_types WHERE id = '{DocTypeId}';");
        }
    }
}
