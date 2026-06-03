using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ManufacturingAI.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddApiKeyPrefix : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "KeyPrefix",
                table: "AppApiKeys",
                type: "text",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "KeyPrefix",
                table: "AppApiKeys");
        }
    }
}
