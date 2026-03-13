using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OcrSystem.Infrastructure.Persistence.Migrations
{
    [Migration("20260310000004_AddCheckboxField")]
    public partial class AddCheckboxField : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "is_checkbox",
                table: "field_mapping_configs",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "is_checkbox",
                table: "field_mapping_configs");
        }
    }
}
