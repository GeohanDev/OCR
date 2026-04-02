using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OcrSystem.Infrastructure.Persistence.Migrations
{
    [Migration("20260316000002_AddReuploadRequired")]
    public partial class AddReuploadRequired : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "reupload_required",
                table: "documents",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "reupload_required",
                table: "documents");
        }
    }
}
