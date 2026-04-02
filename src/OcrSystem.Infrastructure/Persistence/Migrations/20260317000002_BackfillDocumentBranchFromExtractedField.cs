using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OcrSystem.Infrastructure.Persistence.Migrations
{
    [Migration("20260317000002_BackfillDocumentBranchFromExtractedField")]
    public partial class BackfillDocumentBranchFromExtractedField : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Re-assign each document's branch_id using the extracted branch field value.
            // Matches by branch code, Acumatica ID, OR branch name (case-insensitive) so that
            // both BranchID-keyed fields (code match) and BranchName-keyed fields (name match) work.
            // Uses the latest OCR result per document and prefers corrected_value over raw/normalized.
            migrationBuilder.Sql(@"
                UPDATE documents d
                SET    branch_id = matched.branch_id
                FROM (
                    SELECT DISTINCT ON (ocr.document_id)
                        ocr.document_id,
                        b.id AS branch_id
                    FROM   ocr_results ocr
                    JOIN   extracted_fields ef  ON ef.ocr_result_id  = ocr.id
                    JOIN   field_mapping_configs fmc ON fmc.id        = ef.field_mapping_config_id
                    JOIN   branches b ON (
                               UPPER(b.acumatica_branch_id) = UPPER(TRIM(COALESCE(ef.corrected_value, ef.normalized_value, ef.raw_value)))
                            OR UPPER(b.branch_code)         = UPPER(TRIM(COALESCE(ef.corrected_value, ef.normalized_value, ef.raw_value)))
                            OR UPPER(b.branch_name)         = UPPER(TRIM(COALESCE(ef.corrected_value, ef.normalized_value, ef.raw_value)))
                           )
                    WHERE (
                               LOWER(fmc.erp_mapping_key) = 'branchid'
                            OR LOWER(fmc.erp_mapping_key) LIKE '%:branchid'
                            OR LOWER(fmc.erp_mapping_key) = 'company:branchname'
                            OR LOWER(fmc.erp_mapping_key) LIKE '%:branchname'
                          )
                      AND TRIM(COALESCE(ef.corrected_value, ef.normalized_value, ef.raw_value)) <> ''
                    ORDER BY ocr.document_id, ocr.created_at DESC
                ) matched
                WHERE  d.id         = matched.document_id
                  AND  d.is_deleted = false;
            ");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Not reversible — cannot distinguish backfilled from originally-set values.
        }
    }
}
