using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OcrSystem.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddErpResponseField : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "erp_response_field",
                table: "field_mapping_configs",
                type: "character varying(250)",
                maxLength: 250,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "erp_response_field",
                table: "validation_results",
                type: "character varying(250)",
                maxLength: 250,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "erp_response_field",
                table: "field_mapping_configs");

            migrationBuilder.DropColumn(
                name: "erp_response_field",
                table: "validation_results");
        }
    }
}
