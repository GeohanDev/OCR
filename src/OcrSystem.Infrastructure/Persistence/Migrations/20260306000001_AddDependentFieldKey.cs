using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using OcrSystem.Infrastructure.Persistence;

#nullable disable

namespace OcrSystem.Infrastructure.Persistence.Migrations
{
    [DbContext(typeof(AppDbContext))]
    [Migration("20260306000001_AddDependentFieldKey")]
    /// <inheritdoc />
    public partial class AddDependentFieldKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "dependent_field_key",
                table: "field_mapping_configs",
                type: "character varying(150)",
                maxLength: 150,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "dependent_field_key",
                table: "field_mapping_configs");
        }
    }
}
