using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OcrSystem.Infrastructure.Persistence.Migrations
{
    [Migration("20260319000001_AddVendorAgingSnapshots")]
    public partial class AddVendorAgingSnapshots : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "vendor_aging_snapshots",
                columns: table => new
                {
                    id = table.Column<Guid>(nullable: false, defaultValueSql: "gen_random_uuid()"),
                    vendor_local_id = table.Column<string>(maxLength: 500, nullable: false),
                    acumatica_vendor_id = table.Column<string>(maxLength: 100, nullable: true),
                    vendor_name = table.Column<string>(maxLength: 500, nullable: false),
                    snapshot_date = table.Column<DateOnly>(nullable: false),
                    current_amount = table.Column<decimal>(precision: 18, scale: 2, nullable: false),
                    aging_30 = table.Column<decimal>(precision: 18, scale: 2, nullable: false),
                    aging_60 = table.Column<decimal>(precision: 18, scale: 2, nullable: false),
                    aging_90_plus = table.Column<decimal>(precision: 18, scale: 2, nullable: false),
                    total_outstanding = table.Column<decimal>(precision: 18, scale: 2, nullable: false),
                    captured_at = table.Column<DateTimeOffset>(nullable: false),
                },
                constraints: table => table.PrimaryKey("PK_vendor_aging_snapshots", x => x.id));

            migrationBuilder.CreateIndex(
                name: "IX_vendor_aging_snapshots_snapshot_date",
                table: "vendor_aging_snapshots",
                column: "snapshot_date");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "vendor_aging_snapshots");
        }
    }
}
