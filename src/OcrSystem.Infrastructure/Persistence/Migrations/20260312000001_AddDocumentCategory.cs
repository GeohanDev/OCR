using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OcrSystem.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDocumentCategory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "category",
                table: "document_types",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // Assign VendorStatement category (1) to any existing document type
            // whose type key is "VendorStatement" (case-insensitive).
            migrationBuilder.Sql("""
                UPDATE document_types
                SET category = 1
                WHERE LOWER(type_key) = 'vendorstatement';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "category",
                table: "document_types");
        }
    }
}
