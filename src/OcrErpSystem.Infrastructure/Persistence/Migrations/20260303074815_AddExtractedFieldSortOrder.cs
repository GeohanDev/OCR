using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OcrErpSystem.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddExtractedFieldSortOrder : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SortOrder",
                table: "extracted_fields",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SortOrder",
                table: "extracted_fields");
        }
    }
}
