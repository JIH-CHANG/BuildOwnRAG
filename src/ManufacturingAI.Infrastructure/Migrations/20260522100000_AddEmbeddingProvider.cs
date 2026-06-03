using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ManufacturingAI.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddEmbeddingProvider : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Settings_EmbeddingProvider",
                table: "Tenants",
                type: "text",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Settings_EmbeddingProvider",
                table: "Tenants");
        }
    }
}
