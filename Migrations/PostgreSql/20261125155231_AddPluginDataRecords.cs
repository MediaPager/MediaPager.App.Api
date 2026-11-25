using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MediaPager.App.Api.Migrations.PostgreSql
{
    /// <inheritdoc />
    public partial class AddPluginDataRecords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PluginData",
                columns: table => new
                {
                    PluginId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Collection = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Key = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Json = table.Column<string>(type: "text", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PluginData", x => new { x.PluginId, x.Collection, x.Key });
                });

            migrationBuilder.CreateIndex(
                name: "IX_PluginData_PluginId_Collection_UpdatedAt",
                table: "PluginData",
                columns: new[] { "PluginId", "Collection", "UpdatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PluginData");
        }
    }
}
