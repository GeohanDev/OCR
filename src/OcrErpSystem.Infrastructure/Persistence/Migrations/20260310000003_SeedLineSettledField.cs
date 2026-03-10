using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OcrErpSystem.Infrastructure.Persistence.Migrations
{
    [Migration("20260310000003_SeedLineSettledField")]
    public partial class SeedLineSettledField : Migration
    {
        // Same DocTypeId as SeedVendorStatementFields
        private static readonly Guid DocTypeId = new("a1b2c3d4-e5f6-7890-abcd-ef1234567890");
        private static readonly Guid FieldId   = new("11111111-0001-0001-0001-000000000013");

        protected override void Up(MigrationBuilder migrationBuilder)
        {
            var now = DateTimeOffset.UtcNow;

            // Add a manual-entry, multi-value "Settled" column to the vendor statement line-items.
            // is_manual_entry = true  → OCR/Claude skips it; reviewer ticks it per row.
            // allow_multiple  = true  → one placeholder created per table row by OcrPipelineService.
            migrationBuilder.Sql($@"
                INSERT INTO field_mapping_configs
                    (id, document_type_id, field_name, display_label, regex_pattern, keyword_anchor,
                     erp_mapping_key, erp_response_field, dependent_field_key,
                     is_required, allow_multiple, is_manual_entry,
                     confidence_threshold, display_order, is_active, created_at, updated_at)
                VALUES
                    ('{FieldId}', '{DocTypeId}', 'lineSettled', 'Settled', '', '',
                     '', NULL, NULL,
                     false, true, true,
                     1.0, 130, true, '{now:o}', '{now:o}')
                ON CONFLICT DO NOTHING;
            ");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql($"DELETE FROM field_mapping_configs WHERE id = '{FieldId}';");
        }
    }
}
