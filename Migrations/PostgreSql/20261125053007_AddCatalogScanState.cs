using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MediaPager.App.Api.Migrations.PostgreSql
{
    /// <inheritdoc />
    public partial class AddCatalogScanState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "Confidence",
                table: "CatalogItems",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Fingerprint",
                table: "CatalogItems",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastScannedAt",
                table: "CatalogItems",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastSeenAt",
                table: "CatalogItems",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ScanSignature",
                table: "CatalogItems",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_CatalogItems_CatalogId_LastSeenAt",
                table: "CatalogItems",
                columns: new[] { "CatalogId", "LastSeenAt" });

            migrationBuilder.CreateIndex(
                name: "IX_CatalogItems_Fingerprint",
                table: "CatalogItems",
                column: "Fingerprint");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_CatalogItems_CatalogId_LastSeenAt",
                table: "CatalogItems");

            migrationBuilder.DropIndex(
                name: "IX_CatalogItems_Fingerprint",
                table: "CatalogItems");

            migrationBuilder.DropColumn(
                name: "Confidence",
                table: "CatalogItems");

            migrationBuilder.DropColumn(
                name: "Fingerprint",
                table: "CatalogItems");

            migrationBuilder.DropColumn(
                name: "LastScannedAt",
                table: "CatalogItems");

            migrationBuilder.DropColumn(
                name: "LastSeenAt",
                table: "CatalogItems");

            migrationBuilder.DropColumn(
                name: "ScanSignature",
                table: "CatalogItems");
        }
    }
}
