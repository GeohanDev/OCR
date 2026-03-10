using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OcrErpSystem.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    [Migration("20260309000001_AddVendors")]
    public partial class AddVendors : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "vendors",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    acumatica_vendor_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    vendor_name = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    address_line1 = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    address_line2 = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    city = table.Column<string>(type: "character varying(250)", maxLength: 250, nullable: true),
                    state = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    postal_code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    country = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    payment_terms = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    last_synced_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_vendors", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_vendors_acumatica_vendor_id",
                table: "vendors",
                column: "acumatica_vendor_id",
                unique: true);

            migrationBuilder.AddColumn<Guid>(
                name: "vendor_id",
                table: "documents",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "vendor_name",
                table: "documents",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddForeignKey(
                name: "FK_documents_vendors_vendor_id",
                table: "documents",
                column: "vendor_id",
                principalTable: "vendors",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.CreateIndex(
                name: "IX_documents_vendor_id",
                table: "documents",
                column: "vendor_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_documents_vendors_vendor_id",
                table: "documents");

            migrationBuilder.DropIndex(
                name: "IX_documents_vendor_id",
                table: "documents");

            migrationBuilder.DropColumn(
                name: "vendor_id",
                table: "documents");

            migrationBuilder.DropColumn(
                name: "vendor_name",
                table: "documents");

            migrationBuilder.DropTable(name: "vendors");
        }
    }
}
