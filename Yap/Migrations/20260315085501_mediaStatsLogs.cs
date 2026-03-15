using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yap.Migrations
{
    /// <inheritdoc />
    public partial class mediaStatsLogs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MediaUploadLogs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Date = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Username = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    OriginalFileName = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    StoredFileName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    FileSize = table.Column<long>(type: "INTEGER", nullable: false),
                    FileType = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Extension = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    CompressDurationMs = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MediaUploadLogs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MediaUploadLogs_Date",
                table: "MediaUploadLogs",
                column: "Date");

            migrationBuilder.CreateIndex(
                name: "IX_MediaUploadLogs_FileType",
                table: "MediaUploadLogs",
                column: "FileType");

            migrationBuilder.CreateIndex(
                name: "IX_MediaUploadLogs_UserId",
                table: "MediaUploadLogs",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MediaUploadLogs");
        }
    }
}
