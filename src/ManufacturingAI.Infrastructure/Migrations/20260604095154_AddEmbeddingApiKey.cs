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
            // Reconcile TenantSettings columns that the model/snapshot expect but no
            // applied migration ever created (their original migrations shipped without
            // Designer files and were silently skipped). Idempotent so it is safe on both
            // fresh installs and older databases that may already have these columns.
            migrationBuilder.Sql(
                "ALTER TABLE \"Tenants\" ADD COLUMN IF NOT EXISTS \"Settings_EmbeddingApiKey\" text NOT NULL DEFAULT '';");
            migrationBuilder.Sql(
                "ALTER TABLE \"Tenants\" ADD COLUMN IF NOT EXISTS \"Settings_EmbeddingProvider\" text NOT NULL DEFAULT '';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "Settings_EmbeddingApiKey", table: "Tenants");
            migrationBuilder.DropColumn(name: "Settings_EmbeddingProvider", table: "Tenants");
        }
    }
}
