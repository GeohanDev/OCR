using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OcrSystem.Infrastructure.Persistence.Migrations
{
    [Migration("20260316000001_FixVendorStatementCategory")]
    public partial class FixVendorStatementCategory : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The seed migration that inserted 'vendor_statement' did not set the
            // category column, so it defaulted to 0 (General).  Set it to 1 (VendorStatement)
            // so the cash-flow aging report can locate it.
            migrationBuilder.Sql(@"
                UPDATE document_types
                SET    category = 1
                WHERE  type_key  = 'vendor_statement'
                  AND  category  = 0;
            ");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                UPDATE document_types
                SET    category = 0
                WHERE  type_key  = 'vendor_statement';
            ");
        }
    }
}
