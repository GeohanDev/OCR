using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OcrSystem.Infrastructure.Persistence.Migrations
{
    [Migration("20260325000001_AddValidationQueue")]
    public partial class AddValidationQueue : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "validation_queue",
                columns: table => new
                {
                    id = table.Column<Guid>(nullable: false, defaultValueSql: "gen_random_uuid()"),
                    document_id = table.Column<Guid>(nullable: false),
                    document_name = table.Column<string>(maxLength: 500, nullable: false),
                    status = table.Column<string>(maxLength: 50, nullable: false, defaultValue: "Pending"),
                    acumatica_token = table.Column<string>(nullable: true),
                    created_at = table.Column<DateTimeOffset>(nullable: false),
                    started_at = table.Column<DateTimeOffset>(nullable: true),
                    completed_at = table.Column<DateTimeOffset>(nullable: true),
                    error_message = table.Column<string>(nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_validation_queue", x => x.id);
                    table.ForeignKey(
                        name: "FK_validation_queue_documents",
                        column: x => x.document_id,
                        principalTable: "documents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_validation_queue_document_id",
                table: "validation_queue",
                column: "document_id");

            migrationBuilder.CreateIndex(
                name: "IX_validation_queue_status",
                table: "validation_queue",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "IX_validation_queue_created_at",
                table: "validation_queue",
                column: "created_at");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "validation_queue");
        }
    }
}
