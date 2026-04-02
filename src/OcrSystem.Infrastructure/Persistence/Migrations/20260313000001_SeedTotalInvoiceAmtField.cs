using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OcrSystem.Infrastructure.Persistence.Migrations
{
    [Migration("20260313000001_SeedTotalInvoiceAmtField")]
    public partial class SeedTotalInvoiceAmtField : Migration
    {
        private static readonly Guid DocTypeId = new("a1b2c3d4-e5f6-7890-abcd-ef1234567890");
        private static readonly Guid FieldId   = new("11111111-0001-0001-0001-000000000013");

        protected override void Up(MigrationBuilder migrationBuilder)
        {
            var now = DateTimeOffset.UtcNow;
            migrationBuilder.Sql($@"
                INSERT INTO field_mapping_configs
                    (id, document_type_id, field_name, display_label, regex_pattern, keyword_anchor,
                     erp_mapping_key, erp_response_field, dependent_field_key,
                     is_required, allow_multiple, confidence_threshold, display_order, is_active, created_at, updated_at)
                VALUES
                    ('{FieldId}', '{DocTypeId}', 'totalInvoiceAmount', 'Total Invoice Amount',
                     '[\d,]+\.\d{{2}}', 'Total Invoice',
                     'VendorStatement:TotalInvoiceAmount', NULL, NULL,
                     false, false, 0.70, 35, true, '{now:o}', '{now:o}')
                ON CONFLICT DO NOTHING;
            ");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql($"DELETE FROM field_mapping_configs WHERE id = '{FieldId}';");
        }
    }
}
