using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace UrlShortener.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ClickEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Code = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Referrer = table.Column<string>(type: "TEXT", nullable: true),
                    UserAgent = table.Column<string>(type: "TEXT", nullable: true),
                    IpHash = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClickEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ShortUrls",
                columns: table => new
                {
                    Code = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    OriginalUrl = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShortUrls", x => x.Code);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ClickEvents_Code",
                table: "ClickEvents",
                column: "Code");

            migrationBuilder.CreateIndex(
                name: "IX_ShortUrls_IdempotencyKey",
                table: "ShortUrls",
                column: "IdempotencyKey",
                unique: true,
                filter: "IdempotencyKey IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ClickEvents");

            migrationBuilder.DropTable(
                name: "ShortUrls");
        }
    }
}
