using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MediaPager.App.Api.Migrations.PostgreSql
{
    /// <inheritdoc />
    public partial class AddMusicCatalogHierarchy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DiscCount",
                table: "CatalogItems",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DiscNumber",
                table: "CatalogItems",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Isrc",
                table: "CatalogItems",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MusicBrainzId",
                table: "CatalogItems",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TrackCount",
                table: "CatalogItems",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TrackNumber",
                table: "CatalogItems",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CatalogItemMusicArtistCredits",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CatalogItemId = table.Column<int>(type: "integer", nullable: false),
                    ArtistCatalogItemId = table.Column<int>(type: "integer", nullable: false),
                    Role = table.Column<string>(type: "text", nullable: false),
                    Position = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CatalogItemMusicArtistCredits", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CatalogItemMusicArtistCredits_CatalogItems_ArtistCatalogIte~",
                        column: x => x.ArtistCatalogItemId,
                        principalTable: "CatalogItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CatalogItemMusicArtistCredits_CatalogItems_CatalogItemId",
                        column: x => x.CatalogItemId,
                        principalTable: "CatalogItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CatalogItems_CatalogId_Kind_MusicBrainzId",
                table: "CatalogItems",
                columns: new[] { "CatalogId", "Kind", "MusicBrainzId" });

            migrationBuilder.CreateIndex(
                name: "IX_CatalogItemMusicArtistCredits_ArtistCatalogItemId_Role",
                table: "CatalogItemMusicArtistCredits",
                columns: new[] { "ArtistCatalogItemId", "Role" });

            migrationBuilder.CreateIndex(
                name: "IX_CatalogItemMusicArtistCredits_CatalogItemId_Role_Position",
                table: "CatalogItemMusicArtistCredits",
                columns: new[] { "CatalogItemId", "Role", "Position" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CatalogItemMusicArtistCredits");

            migrationBuilder.DropIndex(
                name: "IX_CatalogItems_CatalogId_Kind_MusicBrainzId",
                table: "CatalogItems");

            migrationBuilder.DropColumn(
                name: "DiscCount",
                table: "CatalogItems");

            migrationBuilder.DropColumn(
                name: "DiscNumber",
                table: "CatalogItems");

            migrationBuilder.DropColumn(
                name: "Isrc",
                table: "CatalogItems");

            migrationBuilder.DropColumn(
                name: "MusicBrainzId",
                table: "CatalogItems");

            migrationBuilder.DropColumn(
                name: "TrackCount",
                table: "CatalogItems");

            migrationBuilder.DropColumn(
                name: "TrackNumber",
                table: "CatalogItems");
        }
    }
}
