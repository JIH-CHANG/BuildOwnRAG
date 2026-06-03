using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ManufacturingAI.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddEmbeddingApiKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Settings_EmbeddingApiKey",
                table: "Tenants",
                type: "text",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Settings_EmbeddingApiKey",
                table: "Tenants");
        }
    }
}
