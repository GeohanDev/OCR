using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using OcrSystem.Infrastructure.Persistence;

#nullable disable

namespace OcrSystem.Infrastructure.Persistence.Migrations
{
    [DbContext(typeof(AppDbContext))]
    [Migration("20260310000002_AddTrashColumns")]
    public partial class AddTrashColumns : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "deleted_at", table: "documents",
                type: "timestamp with time zone", nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "is_deleted", table: "field_mapping_configs",
                type: "boolean", nullable: false, defaultValue: false);
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "deleted_at", table: "field_mapping_configs",
                type: "timestamp with time zone", nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "is_deleted", table: "document_types",
                type: "boolean", nullable: false, defaultValue: false);
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "deleted_at", table: "document_types",
                type: "timestamp with time zone", nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "deleted_at", table: "documents");
            migrationBuilder.DropColumn(name: "is_deleted", table: "field_mapping_configs");
            migrationBuilder.DropColumn(name: "deleted_at", table: "field_mapping_configs");
            migrationBuilder.DropColumn(name: "is_deleted", table: "document_types");
            migrationBuilder.DropColumn(name: "deleted_at", table: "document_types");
        }
    }
}
