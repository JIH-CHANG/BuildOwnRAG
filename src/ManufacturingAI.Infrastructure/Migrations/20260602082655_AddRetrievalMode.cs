using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ManufacturingAI.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddRetrievalMode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Settings_RetrievalMode",
                table: "Tenants",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Hybrid");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Settings_RetrievalMode",
                table: "Tenants");
        }
    }
}
