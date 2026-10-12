using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MediaPager.App.Api.Migrations.PostgreSql
{
    /// <inheritdoc />
    public partial class AddCatalogExternalIdentifiers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ImdbId",
                table: "CatalogItems",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Isbn",
                table: "CatalogItems",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ImdbId",
                table: "CatalogItems");

            migrationBuilder.DropColumn(
                name: "Isbn",
                table: "CatalogItems");
        }
    }
}
