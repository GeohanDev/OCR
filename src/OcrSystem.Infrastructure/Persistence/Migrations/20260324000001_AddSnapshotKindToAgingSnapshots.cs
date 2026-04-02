using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OcrSystem.Infrastructure.Persistence.Migrations
{
    [Migration("20260324000001_AddSnapshotKindToAgingSnapshots")]
    public partial class AddSnapshotKindToAgingSnapshots : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "snapshot_kind",
                table: "vendor_aging_snapshots",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateOnly>(
                name: "statement_date",
                table: "vendor_aging_snapshots",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "aging_90",
                table: "vendor_aging_snapshots",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.CreateIndex(
                name: "IX_vendor_aging_snapshots_snapshot_date_snapshot_kind",
                table: "vendor_aging_snapshots",
                columns: new[] { "snapshot_date", "snapshot_kind" });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_vendor_aging_snapshots_snapshot_date_snapshot_kind",
                table: "vendor_aging_snapshots");

            migrationBuilder.DropColumn(name: "snapshot_kind", table: "vendor_aging_snapshots");
            migrationBuilder.DropColumn(name: "statement_date", table: "vendor_aging_snapshots");
            migrationBuilder.DropColumn(name: "aging_90", table: "vendor_aging_snapshots");
        }
    }
}
