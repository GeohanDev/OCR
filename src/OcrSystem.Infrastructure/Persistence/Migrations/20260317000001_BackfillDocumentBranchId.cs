using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OcrSystem.Infrastructure.Persistence.Migrations
{
    [Migration("20260317000001_BackfillDocumentBranchId")]
    public partial class BackfillDocumentBranchId : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Propagate the uploading user's branch_id onto documents that have none set.
            // This covers documents uploaded before branch sync was working.
            migrationBuilder.Sql(@"
                UPDATE documents d
                SET    branch_id = u.branch_id
                FROM   users u
                WHERE  d.uploaded_by   = u.id
                  AND  d.branch_id     IS NULL
                  AND  u.branch_id     IS NOT NULL
                  AND  d.is_deleted    = false;
            ");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Not reversible — we cannot distinguish backfilled from originally-set values.
        }
    }
}
